#!/usr/bin/env python3
"""Regression checks for versioning, manifest validation, and artifact integrity."""

from contextlib import redirect_stdout
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET
import zipfile

import release


class ReleaseTests(unittest.TestCase):
    def test_version_channels(self):
        self.assertEqual("1.2.3-alpha", release.resolve_version("1.2.3", "alpha"))
        self.assertEqual("1.2.3-beta.2", release.resolve_version("1.2.3-beta.2", "beta"))
        self.assertEqual("1.2.3", release.resolve_version("1.2.3", "stable"))

    def test_invalid_versions_and_channel_mismatches(self):
        for version in ("v1.2.3", "01.2.3", "1.2", "1.2.3-alpha.01", "1.2.3+build", "1.2.3-", "1.2.3;echo BAD"):
            with self.subTest(version=version), self.assertRaises(ValueError):
                release.validate_version(version)
        for version, channel in (("1.2.3-beta.1", "alpha"), ("1.2.3-alpha", "stable"), ("1.2.3", "rc")):
            with self.subTest(version=version, channel=channel), self.assertRaises(ValueError):
                release.resolve_version(version, channel)

    def test_project_manifest_validates_paths_and_duplicates(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            project = root / "src/TicketFlow.Core/TicketFlow.Core.csproj"
            project.parent.mkdir(parents=True)
            project.write_text("<Project />")
            manifest = root / "projects.tsv"
            valid = "TicketFlow.Core\tsrc/TicketFlow.Core/TicketFlow.Core.csproj\n"
            manifest.write_text(valid)
            self.assertEqual(1, len(release.read_projects(manifest, root)))
            for content in (valid + valid, "TicketFlow.Core\t../elsewhere.csproj\n", "Other.Core\tsrc/TicketFlow.Core/TicketFlow.Core.csproj\n", "TicketFlow.Core src/TicketFlow.Core/TicketFlow.Core.csproj\n", ""):
                manifest.write_text(content)
                with self.subTest(content=content), self.assertRaises(ValueError):
                    release.read_projects(manifest, root)

    def write_packages(self, directory, version="0.1.0-alpha.1", author="Kyle Crandall", dependency_version=None):
        for extension in ("nupkg", "snupkg"):
            path = directory / f"TicketFlow.Core.{version}.{extension}"
            dependencies = ""
            if dependency_version:
                dependencies = f'<dependencies><group targetFramework="net11.0"><dependency id="TicketFlow.Core" version="{dependency_version}" /></group></dependencies>'
            metadata = f'<package><metadata><id>TicketFlow.Core</id><version>{version}</version><authors>{author}</authors><license type="expression">MIT</license><repository type="git" url="https://github.com/kcrandall/TicketFlow" /><readme>README.md</readme>{dependencies}</metadata></package>'
            with zipfile.ZipFile(path, "w") as archive:
                archive.writestr("TicketFlow.Core.nuspec", metadata)
                if extension == "nupkg":
                    archive.writestr("README.md", "Package documentation")
                    archive.writestr("lib/net11.0/TicketFlow.Core.dll", "test assembly fixture")
                    archive.writestr("lib/net11.0/TicketFlow.Core.xml", "<doc />")
                else:
                    archive.writestr("lib/net11.0/TicketFlow.Core.pdb", "test symbols fixture")

    def test_complete_artifacts_and_checksums(self):
        with tempfile.TemporaryDirectory() as temporary, patch.object(release, "read_projects", return_value=[("TicketFlow.Core", "src/TicketFlow.Core/TicketFlow.Core.csproj")]), redirect_stdout(io.StringIO()):
            directory = Path(temporary)
            self.write_packages(directory)
            release.write_manifest(directory, "0.1.0-alpha.1")
            self.assertEqual("0.1.0-alpha.1", release.verify_manifest(directory))
            manifest_path = directory / "release-manifest.json"
            manifest = json.loads(manifest_path.read_text())
            manifest["files"][0]["sha256"] = "0" * 64
            manifest_path.write_text(json.dumps(manifest))
            with self.assertRaisesRegex(ValueError, "Checksum mismatch"):
                release.verify_manifest(directory)

    def test_missing_and_extra_artifacts_rejected(self):
        with tempfile.TemporaryDirectory() as temporary, patch.object(release, "read_projects", return_value=[("TicketFlow.Core", "src/TicketFlow.Core/TicketFlow.Core.csproj")]), redirect_stdout(io.StringIO()):
            directory = Path(temporary)
            self.write_packages(directory)
            (directory / "Unexpected.0.1.0.nupkg").write_bytes(b"not a package")
            with self.assertRaisesRegex(ValueError, "Package set mismatch"):
                release.validate_packages(directory, "0.1.0-alpha.1")
            (directory / "Unexpected.0.1.0.nupkg").unlink()
            (directory / "TicketFlow.Core.0.1.0-alpha.1.snupkg").unlink()
            with self.assertRaisesRegex(ValueError, "Package set mismatch"):
                release.validate_packages(directory, "0.1.0-alpha.1")

    def test_wrong_metadata_and_internal_dependency_rejected(self):
        with tempfile.TemporaryDirectory() as temporary, patch.object(release, "read_projects", return_value=[("TicketFlow.Core", "src/TicketFlow.Core/TicketFlow.Core.csproj")]):
            directory = Path(temporary)
            self.write_packages(directory, author="Unexpected author")
            with self.assertRaisesRegex(ValueError, "Incorrect author"):
                release.validate_packages(directory, "0.1.0-alpha.1")
            self.write_packages(directory, dependency_version="0.2.0")
            with self.assertRaisesRegex(ValueError, "Internal dependency version differs"):
                release.validate_packages(directory, "0.1.0-alpha.1")

    def test_pack_rejects_missing_option_value(self):
        for option in ("--version", "--output", "--prerelease", "--configuration"):
            result = subprocess.run(["bash", str(release.ROOT / "scripts/pack.sh"), option], capture_output=True, text=True)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("Missing value", result.stderr)

    def test_publish_rejects_local_execution(self):
        result = subprocess.run(["bash", str(release.ROOT / "scripts/publish-nuget.sh")],
                                env={"PATH": "/usr/bin:/bin"}, capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("restricted", result.stderr)

    def test_consumer_confines_ticketflow_to_local_exact_versions(self):
        entries = [("TicketFlow.Core", "src/TicketFlow.Core/TicketFlow.Core.csproj"),
                   ("TicketFlow", "src/TicketFlow/TicketFlow.csproj")]
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            consumer = root / "consumer"
            consumer.mkdir()
            feed = root / "local feed & packages"
            release.write_consumer_files(consumer, feed, "0.1.0-alpha.1", entries)
            configuration = ET.parse(consumer / "NuGet.Config")
            sources = {item.attrib["key"]: item.attrib["value"] for item in configuration.findall(".//packageSources/add")}
            self.assertEqual(str(feed.resolve()), sources["ticketflow-local"])
            self.assertEqual("https://api.nuget.org/v3/index.json", sources["nuget.org"])
            mappings = {item.attrib["key"]: [package.attrib["pattern"] for package in item.findall("package")]
                        for item in configuration.findall(".//packageSourceMapping/packageSource")}
            self.assertEqual([item[0] for item in entries], mappings["ticketflow-local"])
            self.assertEqual(["*"], mappings["nuget.org"])
            references = ET.parse(consumer / "Consumer.csproj").findall(".//PackageReference")
            self.assertEqual([item[0] for item in entries], [reference.attrib["Include"] for reference in references])
            self.assertTrue(all(reference.attrib["Version"] == "[0.1.0-alpha.1]" for reference in references))

    def test_consumer_restore_uses_mapping_configuration_without_source_override(self):
        with patch.object(release, "verify_manifest", return_value="0.1.0-alpha.1"), \
             patch.object(release, "read_projects", return_value=[("TicketFlow.Core", "unused.csproj")]), \
             patch.object(release, "run") as run_mock, redirect_stdout(io.StringIO()):
            release.consumer_smoke(Path("artifacts/nuget"))
        restore_call = run_mock.call_args_list[0]
        arguments = restore_call.args[0]
        self.assertIn("--configfile", arguments)
        self.assertNotIn("--source", arguments)
        self.assertEqual(restore_call.kwargs["cwd"] / "NuGet.Config", Path(arguments[arguments.index("--configfile") + 1]))

    def test_pack_selects_repository_sdk_from_foreign_working_directory(self):
        # Fake only the external tools; exercise the actual shell orchestration
        # from a directory that does not contain TicketFlow's global.json.
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            tools = root / "tools"
            tools.mkdir()
            log = root / "dotnet-cwd.log"
            (tools / "dotnet").write_text('#!/usr/bin/env bash\nprintf "%s\\n" "$PWD" >> "$TICKETFLOW_TEST_CWD_LOG"\n')
            (tools / "python3").write_text('#!/usr/bin/env bash\nif [[ "${2:-}" == resolve-version ]]; then printf "0.1.0-alpha.1\\n"; fi\n')
            for tool in tools.iterdir():
                tool.chmod(0o755)
            environment = os.environ.copy()
            environment["PATH"] = str(tools) + os.pathsep + environment["PATH"]
            environment["TICKETFLOW_TEST_CWD_LOG"] = str(log)
            result = subprocess.run(["bash", str(release.ROOT / "scripts/pack.sh"), "--skip-tests", "--output", str(root / "output")],
                                    cwd=root, env=environment, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            directories = log.read_text().splitlines()
            self.assertGreaterEqual(len(directories), 3)
            self.assertTrue(all(Path(directory) == release.ROOT for directory in directories))


class WorkflowTests(unittest.TestCase):
    WORKFLOWS = release.ROOT / ".github" / "workflows"

    @staticmethod
    def block(lines, header, indent):
        """Lines nested under `header` (a line at `indent` spaces) until the next line at that indent or shallower."""
        start = lines.index(" " * indent + header)
        body = []
        for line in lines[start + 1:]:
            if line.strip() and len(line) - len(line.lstrip(" ")) <= indent:
                break
            body.append(line)
        return body

    @staticmethod
    def keys(lines, indent):
        return [line.strip().rstrip(":") for line in lines
                if line.strip() and not line.lstrip().startswith("#") and len(line) - len(line.lstrip(" ")) == indent and line.rstrip().endswith(":")]

    def workflow(self, name):
        return (self.WORKFLOWS / name).read_text().splitlines()

    def test_workflows_are_manual_only(self):
        for name in ("ci.yml", "nuget-publish.yml"):
            lines = self.workflow(name)
            triggers = [line.strip().split(":")[0] for line in self.block(lines, "on:", 0)
                        if line.strip() and len(line) - len(line.lstrip(" ")) == 2]
            self.assertEqual(["workflow_dispatch"], triggers, name)

    def test_workflows_check_the_main_ref(self):
        self.assertIn("if: github.ref == 'refs/heads/main'", "\n".join(self.workflow("ci.yml")))
        self.assertIn('"${GITHUB_REF}" != refs/heads/main', "\n".join(self.workflow("nuget-publish.yml")))

    def test_id_token_write_only_on_publish_job(self):
        ci = self.workflow("ci.yml")
        self.assertNotIn("id-token", "\n".join(ci))
        publish = self.workflow("nuget-publish.yml")
        self.assertIn("contents: read", "\n".join(self.block(publish, "permissions:", 0)))
        jobs = self.block(publish, "jobs:", 0)
        for job in self.keys(jobs, 2):
            body = "\n".join(self.block(jobs, job + ":", 2))
            self.assertEqual(1 if job == "publish" else 0, body.count("id-token: write"), job)
        self.assertEqual(1, "\n".join(publish).count("id-token"))

    def test_postgres_tests_are_required_where_configured(self):
        for name in ("ci.yml", "nuget-publish.yml"):
            text = "\n".join(self.workflow(name))
            self.assertIn('TICKETFLOW_REQUIRE_POSTGRES: "1"', text, name)
        self.assertIn("TICKETFLOW_REQUIRE_POSTGRES=1", (release.ROOT / "scripts" / "test-postgres.sh").read_text())


if __name__ == "__main__":
    unittest.main(verbosity=2)

#!/usr/bin/env python3
"""Small, dependency-free lockstep release validation tools."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parent.parent
MANIFEST = ROOT / "scripts/nuget-projects.tsv"
PACKAGE_ID = re.compile(r"TicketFlow(?:\.[A-Za-z0-9]+)*\Z")
VERSION = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\Z")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def validate_version(version):
    match = VERSION.fullmatch(version)
    require(match is not None, f"Invalid release version: {version}. Use canonical SemVer without build metadata.")
    prerelease = match.group(4)
    if prerelease:
        require(all(not part.isdigit() or part == "0" or not part.startswith("0") for part in prerelease.split(".")),
                "Numeric prerelease identifiers cannot have leading zeroes.")
    return version


def resolve_version(version, channel, root=ROOT):
    require(channel in ("alpha", "beta", "stable"), f"Unknown release channel: {channel}")
    if not version:
        version = ET.parse(root / "Directory.Build.props").findtext(".//TicketFlowVersion")
    validate_version(version)
    if channel == "stable":
        require("-" not in version, "Stable releases cannot contain a prerelease identifier.")
        return version
    base, separator, prerelease = version.partition("-")
    if separator:
        require(prerelease == channel or prerelease.startswith(channel + "."),
                f"Version prerelease must match channel {channel}.")
        return version
    return f"{base}-{channel}"


def read_projects(path=MANIFEST, root=ROOT):
    entries = []
    ids, paths = set(), set()
    for line in path.read_text().splitlines():
        if not line.strip() or line.startswith("#"):
            continue
        fields = line.split("\t")
        require(len(fields) == 2, f"Manifest row requires exactly two tab-separated fields: {line}")
        package_id, project = fields
        require(PACKAGE_ID.fullmatch(package_id), f"Invalid package ID: {package_id}")
        require(project.startswith("src/") and project.endswith(".csproj") and ".." not in Path(project).parts,
                f"Invalid project path: {project}")
        require(package_id not in ids and project not in paths, f"Duplicate manifest entry: {line}")
        require((root / project).is_file(), f"Missing package project: {project}")
        ids.add(package_id)
        paths.add(project)
        entries.append((package_id, project))
    require(entries, "Package manifest is empty.")
    return entries


def run(arguments, **kwargs):
    return subprocess.run(arguments, check=True, **kwargs)


def validate_projects():
    entries = read_projects()
    listed = {project: package_id for package_id, project in entries}
    positions = {project: i for i, (_, project) in enumerate(entries)}
    source_projects = sorted(path for path in (ROOT / "src").rglob("*.csproj") if "obj" not in path.parts and "bin" not in path.parts)
    packable = set()
    solution = ET.parse(ROOT / "TicketFlow.slnx")
    solution_projects = {item.attrib["Path"].replace("\\", "/") for item in solution.iter("Project")}
    for path in source_projects:
        relative = path.relative_to(ROOT).as_posix()
        require(relative in solution_projects, f"Source project missing from solution: {relative}")
        result = run(["dotnet", "msbuild", str(path), "-nologo", "-getProperty:IsPackable,PackageId", "-getItem:ProjectReference"],
                     capture_output=True, text=True, cwd=ROOT)
        evaluation = json.loads(result.stdout)
        if evaluation["Properties"]["IsPackable"].lower() == "true":
            packable.add(relative)
            require(relative in listed, f"Packable project missing from manifest: {relative}")
            require(evaluation["Properties"]["PackageId"] == listed[relative], f"Package ID differs from manifest: {relative}")
            require((path.parent / "README.md").is_file(), f"Package README missing: {relative}")
            for reference in evaluation.get("Items", {}).get("ProjectReference", []):
                ref_path = Path(reference["FullPath"]).resolve().relative_to(ROOT).as_posix()
                require(ref_path in listed, f"Package references project outside package manifest: {ref_path}")
                require(positions[ref_path] < positions[relative], f"Manifest dependencies must precede {relative}: {ref_path}")
    require(packable == set(listed), "Manifest contains a non-packable or unsupported source project.")
    for directory in ("src", "tests", "samples"):
        for path in (ROOT / directory).rglob("*.csproj"):
            if "obj" in path.parts or "bin" in path.parts:
                continue
            for reference in ET.parse(path).iter("PackageReference"):
                require(not (reference.attrib.get("Version") or reference.attrib.get("VersionOverride") or
                             reference.find("Version") is not None or reference.find("VersionOverride") is not None),
                        f"Package versions must be central: {path.relative_to(ROOT)}")
    print(f"Validated {len(entries)} package projects and central versions.")


def nuspec(archive):
    names = [name for name in archive.namelist() if name.endswith(".nuspec")]
    require(len(names) == 1, "Package must contain exactly one nuspec.")
    root = ET.fromstring(archive.read(names[0]))
    for element in root.iter():
        element.tag = element.tag.split("}")[-1]
    metadata = root.find("metadata")
    require(metadata is not None, "Package metadata missing.")
    return metadata


def validate_packages(directory, version):
    validate_version(version)
    directory = directory.resolve()
    entries = read_projects()
    expected = {f"{package_id}.{version}.{extension}" for package_id, _ in entries for extension in ("nupkg", "snupkg")}
    actual = {path.name for path in directory.iterdir() if path.suffix in (".nupkg", ".snupkg")}
    require(actual == expected, f"Package set mismatch. Missing: {sorted(expected - actual)}; extra: {sorted(actual - expected)}")
    internal_ids = {package_id for package_id, _ in entries}
    for package_id, _ in entries:
        for extension in ("nupkg", "snupkg"):
            path = directory / f"{package_id}.{version}.{extension}"
            with zipfile.ZipFile(path) as archive:
                require(archive.testzip() is None, f"Corrupt package: {path.name}")
                metadata = nuspec(archive)
                require(metadata.findtext("id") == package_id and metadata.findtext("version") == version,
                        f"Package identity/version mismatch: {path.name}")
                if extension == "snupkg":
                    require(any(name.endswith(".pdb") for name in archive.namelist()), f"Symbols missing: {path.name}")
                    continue
                require(metadata.findtext("authors") == "Kyle Crandall", f"Incorrect author: {path.name}")
                license_element = metadata.find("license")
                require(license_element is not None and license_element.attrib.get("type") == "expression" and license_element.text == "MIT",
                        f"MIT package license missing: {path.name}")
                repository = metadata.find("repository")
                require(repository is not None and repository.attrib.get("type") == "git" and
                        repository.attrib.get("url", "").removesuffix(".git") == "https://github.com/kcrandall/TicketFlow",
                        f"Repository metadata missing: {path.name}")
                require(metadata.findtext("readme") == "README.md" and "README.md" in archive.namelist(), f"README missing: {path.name}")
                require(f"lib/net11.0/{package_id}.dll" in archive.namelist(), f"net11.0 assembly missing: {path.name}")
                require(f"lib/net11.0/{package_id}.xml" in archive.namelist(), f"API documentation missing: {path.name}")
                for dependency in metadata.iter("dependency"):
                    if dependency.attrib.get("id") in internal_ids:
                        require(dependency.attrib.get("version") in (version, f"[{version}, )", f"[{version}]"),
                                f"Internal dependency version differs: {path.name}: {dependency.attrib}")
    print(f"Validated contents and metadata for {len(entries)} packages and symbols at {version}.")


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_manifest(directory, version):
    validate_packages(directory, version)
    files = sorted(path for path in directory.iterdir() if path.suffix in (".nupkg", ".snupkg"))
    manifest = {"schemaVersion": 1, "version": version,
                "files": [{"name": path.name, "sha256": sha256(path)} for path in files]}
    (directory / "release-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"Wrote checksum manifest: {directory / 'release-manifest.json'}")


def verify_manifest(directory):
    manifest = json.loads((directory / "release-manifest.json").read_text())
    require(manifest.get("schemaVersion") == 1, "Unsupported release manifest schema.")
    version = validate_version(manifest["version"])
    validate_packages(directory, version)
    actual = {path.name for path in directory.iterdir() if path.suffix in (".nupkg", ".snupkg")}
    entries = manifest["files"]
    require(len(entries) == len(actual) and {item["name"] for item in entries} == actual,
            "Checksum manifest package set differs.")
    for item in entries:
        require(Path(item["name"]).name == item["name"], "Invalid checksum manifest filename.")
        require(sha256(directory / item["name"]) == item["sha256"], f"Checksum mismatch: {item['name']}")
    print("Verified immutable release artifact checksums.")
    return version


def write_consumer_files(consumer, directory, version, entries):
    references = "\n".join(f'    <PackageReference Include="{package_id}" Version="[{version}]" />' for package_id, _ in entries)
    (consumer / "Consumer.csproj").write_text(
        '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup><TargetFramework>net11.0</TargetFramework>'
        '<OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>'
        '<TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>\n  <ItemGroup>\n' + references + '\n  </ItemGroup>\n</Project>\n')
    (consumer / "global.json").write_text((ROOT / "global.json").read_text())

    configuration = ET.Element("configuration")
    sources = ET.SubElement(configuration, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="ticketflow-local", value=str(directory.resolve()))
    ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
    mappings = ET.SubElement(configuration, "packageSourceMapping")
    local_mapping = ET.SubElement(mappings, "packageSource", key="ticketflow-local")
    for package_id, _ in entries:
        # Exact-ID mappings take precedence over nuget.org's wildcard. Every
        # TicketFlow package must come from the artifact set under test.
        ET.SubElement(local_mapping, "package", pattern=package_id)
    public_mapping = ET.SubElement(mappings, "packageSource", key="nuget.org")
    ET.SubElement(public_mapping, "package", pattern="*")
    ET.indent(configuration, space="  ")
    ET.ElementTree(configuration).write(consumer / "NuGet.Config", encoding="utf-8", xml_declaration=True)

    package_ids = ", ".join(json.dumps(package_id) for package_id, _ in entries)
    (consumer / "Program.cs").write_text(
        'using System.Reflection;\n'
        f'string[] packages = [{package_ids}];\n'
        'foreach (string name in packages)\n{\n'
        '    Assembly assembly = Assembly.Load(name);\n'
        '    if (assembly.GetExportedTypes().Length == 0)\n'
        '        throw new InvalidOperationException($"No public API in {name}");\n'
        '    Console.WriteLine($"Loaded {name}");\n}\n')


def consumer_smoke(directory):
    version = verify_manifest(directory)
    entries = read_projects()
    with tempfile.TemporaryDirectory(prefix="ticketflow-consumer-") as temporary:
        consumer = Path(temporary)
        write_consumer_files(consumer, directory, version, entries)
        run(["dotnet", "restore", "Consumer.csproj", "--packages", str(consumer / "packages"),
             "--configfile", str(consumer / "NuGet.Config")], cwd=consumer)
        run(["dotnet", "run", "--project", "Consumer.csproj", "--configuration", "Release", "--no-restore"], cwd=consumer)
    print("Local NuGet consumer restored, built, and loaded every package using an isolated cache.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    resolve = subparsers.add_parser("resolve-version")
    resolve.add_argument("--version", default="")
    resolve.add_argument("--prerelease", choices=("alpha", "beta", "stable"), default="alpha")
    subparsers.add_parser("validate-projects")
    for command in ("validate-packages", "write-manifest", "verify-manifest", "consumer-smoke"):
        subparser = subparsers.add_parser(command)
        subparser.add_argument("--package-dir", type=Path, default=ROOT / "artifacts/nuget")
        if command in ("validate-packages", "write-manifest"):
            subparser.add_argument("--version", required=True)
    args = parser.parse_args()
    if args.command == "resolve-version":
        print(resolve_version(args.version, args.prerelease))
    elif args.command == "validate-projects":
        validate_projects()
    elif args.command == "validate-packages":
        validate_packages(args.package_dir, args.version)
    elif args.command == "write-manifest":
        write_manifest(args.package_dir, args.version)
    elif args.command == "verify-manifest":
        verify_manifest(args.package_dir)
    elif args.command == "consumer-smoke":
        consumer_smoke(args.package_dir)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, zipfile.BadZipFile, KeyError, json.JSONDecodeError, subprocess.CalledProcessError) as error:
        print(f"Release validation failed: {error}", file=sys.stderr)
        sys.exit(1)

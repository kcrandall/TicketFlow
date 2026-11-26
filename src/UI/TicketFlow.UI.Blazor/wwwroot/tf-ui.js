// TicketFlow UI progressive enhancements. Everything works without this file.
(() => {
  const media = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
  const syncSystemTheme = () => {
    if (!media) return;
    const dark = media.matches;
    if (document.documentElement.getAttribute('data-tf-mode') === 'system')
      document.documentElement.classList.toggle('dark', dark);
    document.querySelectorAll('.tf-scope[data-tf-mode="system"]').forEach((scope) => {
      scope.classList.toggle('dark', dark);
    });
  };
  syncSystemTheme();
  if (media) {
    if (typeof media.addEventListener === 'function') media.addEventListener('change', syncSystemTheme);
    else if (typeof media.addListener === 'function') media.addListener(syncSystemTheme);
  }
  window.addEventListener('load', () => window.Blazor?.addEventListener?.('enhancedload', syncSystemTheme));

  // Close an open <details> menu when clicking elsewhere, and handle clipboard copy buttons.
  document.addEventListener('click', (event) => {
    document.querySelectorAll('details.tf-menu[open]').forEach((menu) => { if (!menu.contains(event.target)) menu.removeAttribute('open'); });
    const button = event.target instanceof Element ? event.target.closest('[data-copy-target]') : null;
    if (!button || !navigator.clipboard) return;
    const target = document.querySelector(button.getAttribute('data-copy-target'));
    if (!target) return;
    navigator.clipboard.writeText(target.textContent || '').then(() => {
      const label = button.querySelector('span');
      const feedback = button.getAttribute('data-copy-feedback') || 'Copied!';
      if (!label) return;
      const original = label.textContent;
      label.textContent = feedback;
      window.setTimeout(() => { label.textContent = original; }, 1600);
    }).catch(() => {});
  });
  // Submit filter forms when a select changes.
  document.addEventListener('change', (event) => {
    const target = event.target;
    if (target instanceof HTMLSelectElement && target.form && target.form.hasAttribute('data-autosubmit')) target.form.requestSubmit();
  });
})();

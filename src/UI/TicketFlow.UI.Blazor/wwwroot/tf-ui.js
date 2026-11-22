// TicketFlow UI progressive enhancements. Everything works without this file.
(() => {
  // Close an open <details> menu when clicking elsewhere.
  document.addEventListener('click', (event) => {
    document.querySelectorAll('details.tf-menu[open]').forEach((menu) => { if (!menu.contains(event.target)) menu.removeAttribute('open'); });
  });
  // Submit filter forms when a select changes.
  document.addEventListener('change', (event) => {
    const target = event.target;
    if (target instanceof HTMLSelectElement && target.form && target.form.hasAttribute('data-autosubmit')) target.form.requestSubmit();
  });
})();

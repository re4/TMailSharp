(() => {
  const sidebar = document.getElementById('site-sidebar');
  const toggle = document.getElementById('navigation-toggle');
  const backdrop = document.querySelector('.sidebar-backdrop');
  const workspace = document.querySelector('.workspace');
  if (!sidebar || !toggle || !backdrop || !workspace) return;

  const mobile = window.matchMedia('(max-width: 760px)');
  let open = false;

  function close(restoreFocus = true) {
    const wasOpen = open;
    open = false;
    workspace.inert = false;
    document.body.classList.remove('navigation-open');
    toggle.setAttribute('aria-expanded', 'false');
    backdrop.hidden = true;
    sidebar.removeAttribute('role');
    sidebar.removeAttribute('aria-modal');
    if (restoreFocus && wasOpen) toggle.focus();
    sidebar.inert = mobile.matches;
  }

  function show() {
    if (!mobile.matches) return;
    open = true;
    sidebar.inert = false;
    sidebar.setAttribute('role', 'dialog');
    sidebar.setAttribute('aria-modal', 'true');
    document.body.classList.add('navigation-open');
    toggle.setAttribute('aria-expanded', 'true');
    backdrop.hidden = false;
    workspace.inert = true;
    requestAnimationFrame(() => requestAnimationFrame(() => {
      if (open && (document.activeElement === document.body || document.activeElement === toggle)) {
        document.getElementById('navigation-close').focus();
      }
    }));
  }

  toggle.addEventListener('click', show);
  document.querySelectorAll('[data-close-sidebar]').forEach(button => {
    button.addEventListener('click', () => close());
  });
  sidebar.addEventListener('click', event => {
    // Close before mailbox controls open a dialog in the main workspace.
    if (open && event.target.closest('a[href], .mailbox-item, [data-dialog], #add-inbox')) close(false);
  }, true);
  document.addEventListener('keydown', event => {
    if (!open) return;
    if (event.key === 'Escape') {
      event.preventDefault();
      close();
    } else if (event.key === 'Tab') {
      const focusable = [...sidebar.querySelectorAll('a[href], button, input, select, [tabindex="0"]')]
        .filter(element => !element.disabled && element.getClientRects().length);
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (event.shiftKey && (document.activeElement === first || !sidebar.contains(document.activeElement))) {
        event.preventDefault();
        last?.focus();
      } else if (!event.shiftKey && (document.activeElement === last || !sidebar.contains(document.activeElement))) {
        event.preventDefault();
        first?.focus();
      }
    }
  });
  mobile.addEventListener('change', () => {
    const needsFocus = mobile.matches && sidebar.contains(document.activeElement);
    close(false);
    if (needsFocus) toggle.focus();
  });
  document.documentElement.classList.add('navigation-ready');
  close(false);
})();

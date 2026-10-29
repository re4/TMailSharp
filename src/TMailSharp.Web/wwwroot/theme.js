(() => {
  const preferenceKey = 'tmailsharp.theme';

  function applyTheme(value, remember = false) {
    const theme = value === 'light' ? 'light' : 'dark';
    document.documentElement.dataset.theme = theme;
    document.querySelector('meta[name="theme-color"]')?.setAttribute('content', theme === 'dark' ? '#0b1120' : '#f7f9fd');

    const button = document.getElementById('theme-toggle');
    if (button) {
      const label = theme === 'dark' ? 'Switch to light mode' : 'Switch to dark mode';
      button.setAttribute('aria-label', label);
      button.title = label;
      button.querySelector('use')?.setAttribute('href', `/icons.svg#${theme === 'dark' ? 'sun' : 'moon'}`);
    }

    if (remember) {
      try { localStorage.setItem(preferenceKey, theme); } catch { /* The selected theme still works without browser storage. */ }
    }
    document.dispatchEvent(new CustomEvent('tmailthemechange', { detail: { theme } }));
  }

  let initialTheme = 'dark';
  try { initialTheme = localStorage.getItem(preferenceKey) || 'dark'; } catch { /* Dark is the default when storage is unavailable. */ }
  applyTheme(initialTheme);

  document.addEventListener('DOMContentLoaded', () => {
    applyTheme(document.documentElement.dataset.theme);
    document.getElementById('theme-toggle')?.addEventListener('click', () => {
      applyTheme(document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark', true);
    });
  });

  window.addEventListener('storage', event => {
    if (event.key === preferenceKey) applyTheme(event.newValue);
  });
})();

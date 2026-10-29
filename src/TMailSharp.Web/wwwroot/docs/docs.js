const origin = window.location.origin;
document.querySelectorAll('[data-origin]').forEach(element => {
  element.textContent = origin;
});
document.querySelectorAll('[data-shell-origin]').forEach(element => {
  element.textContent = `'${origin.replaceAll("'", "'\\''")}'`;
});

const status = document.getElementById('copy-status');
document.querySelectorAll('pre[data-copyable]').forEach(block => {
  block.tabIndex = 0;
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'docs-copy';
  button.textContent = 'Copy';
  button.setAttribute('aria-label', `Copy ${block.getAttribute('aria-label')}`);
  block.previousElementSibling.append(button);

  let resetTimer;
  button.addEventListener('click', async () => {
    clearTimeout(resetTimer);
    status.textContent = '';
    try {
      await navigator.clipboard.writeText(block.textContent);
      button.textContent = 'Copied';
      status.textContent = 'Example copied to clipboard.';
    } catch {
      const selection = window.getSelection();
      const range = document.createRange();
      range.selectNodeContents(block);
      selection.removeAllRanges();
      selection.addRange(range);
      button.textContent = 'Selected';
      status.textContent = 'Example selected. Press Ctrl+C or use your device’s Copy action.';
    }
    resetTimer = setTimeout(() => { button.textContent = 'Copy'; }, 2500);
  });
});

(() => {
  if (!window.__lt) {
    window.__lt = { mousedown: 0, mouseup: 0, click: 0, dblclick: 0 };
    document.addEventListener('mousedown', () => window.__lt.mousedown++, true);
    document.addEventListener('mouseup', () => window.__lt.mouseup++, true);
    document.addEventListener('click', () => window.__lt.click++, true);
    document.addEventListener('dblclick', () => window.__lt.dblclick++, true);
  }
  return window.__lt;
})()

(() => {
  const info = {
    isTop: window.top === window,
    frames: window.frames.length,
    frameSrcs: Array.from(document.querySelectorAll('iframe')).map(f => String(f.src || '').slice(0, 70)),
    hasVideo: !!document.querySelector('video'),
    counters: window.__lt || null,
  };
  return info;
})()

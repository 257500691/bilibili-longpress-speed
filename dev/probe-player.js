(() => {
  const v = document.querySelector('video');
  const r = v ? v.getBoundingClientRect() : null;
  return {
    ready: document.readyState,
    title: document.title,
    href: location.href,
    videos: document.querySelectorAll('video').length,
    rate: v ? v.playbackRate : null,
    paused: v ? v.paused : null,
    duration: v ? v.duration : null,
    rect: r ? { x: r.x, y: r.y, w: r.width, h: r.height } : null,
    dpr: devicePixelRatio,
    size: [innerWidth, innerHeight],
  };
})()

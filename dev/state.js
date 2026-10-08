(() => {
  const v = document.querySelector('video');
  return { paused: v.paused, rate: v.playbackRate, t: Number(v.currentTime.toFixed(2)) };
})()

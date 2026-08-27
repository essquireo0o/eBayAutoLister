// Photo Box result-first behavior kept separate from app.js because that file is shared with the
// long-scan workstream. The first photograph is the completion of the user's action: bring it into
// view once, then leave the seller's scroll position alone for the rest of the set.
(() => {
  'use strict';

  const strip = document.getElementById('pb-filmstrip');
  const review = strip?.closest('.pb-review');
  const emptyLine = document.getElementById('pb-empty-line');
  const aiButton = document.getElementById('pb-ai');
  if (!strip || !review) return;

  let hadPhotos = !strip.classList.contains('hidden') && strip.children.length > 0;
  const reduceMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;

  const makeEmptyCopyFriendly = () => {
    if (emptyLine && /allow the camera/i.test(emptyLine.textContent || ''))
      emptyLine.textContent = "Scan once. Your phone's live camera appears right here.";
  };

  const makeAiActionObvious = () => {
    if (!aiButton || aiButton.disabled) return;
    const count = strip.children.length;
    const label = count > 1
      ? `✨ Create AI eBay Listing (${count} photos)`
      : '✨ Create AI eBay Listing';
    if (aiButton.textContent !== label) aiButton.textContent = label;
  };

  const reactToPhotos = () => {
    const hasPhotos = !strip.classList.contains('hidden') && strip.children.length > 0;
    if (hasPhotos && !hadPhotos) {
      requestAnimationFrame(() => review.scrollIntoView({
        behavior: reduceMotion ? 'auto' : 'smooth',
        block: 'start'
      }));
    }
    hadPhotos = hasPhotos;
    makeAiActionObvious();
  };

  makeEmptyCopyFriendly();
  makeAiActionObvious();
  new MutationObserver(() => {
    makeEmptyCopyFriendly();
    reactToPhotos();
  }).observe(document.getElementById('photobox-section') || strip, {
    subtree: true,
    childList: true,
    characterData: true,
    attributes: true,
    attributeFilter: ['class']
  });
})();

// Tail-following for panes that stream lines while you are reading them.
//
// The rule is the one every terminal follows: keep the newest line in view while the reader is at
// the bottom, and stop the moment they scroll up to read something. Deciding that from the scroll
// position at the time new lines arrive does not work — by then the content is already in the DOM,
// so a reader who was at the bottom reads as "scrolled up" by exactly the height of what just
// arrived. The pinned state is therefore remembered from the reader's own scrolling, and consulted
// afterwards.
window.scrollTail = {
	// Distance from the bottom, in pixels, still counted as being at the bottom. Fractional heights
	// and zoom leave scrollTop a little short of the arithmetic, and a reader who has not touched the
	// scrollbar must not be treated as having scrolled away.
	threshold: 24,

	// Scrolls the element to the bottom if its reader is still following it. Safe to call on every
	// render: the listener is attached once, and the element remembers its own state.
	follow: function (element) {
		if (!element) {
			return;
		}

		if (!element.dataset.tailBound) {
			element.dataset.tailBound = "1";
			// New panes start pinned, so a transcript follows from its first line without the reader
			// having to scroll down to opt in.
			element.dataset.tailPinned = "1";

			element.addEventListener("scroll", function () {
				const distance = element.scrollHeight - element.scrollTop - element.clientHeight;
				element.dataset.tailPinned = distance <= window.scrollTail.threshold ? "1" : "0";
			}, { passive: true });
		}

		if (element.dataset.tailPinned === "1") {
			// Fires the listener above, which finds the distance at zero and leaves the element pinned.
			element.scrollTop = element.scrollHeight;
		}
	}
};

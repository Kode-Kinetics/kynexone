/**
 * A URL parameter that seeds an editable field, such as People `?search=`.
 *
 * The link decides what the box starts with; after that the box belongs to the person typing. The
 * People page used to re-apply the URL value whenever the box stopped matching it, which is on
 * every keystroke, so anything typed was replaced by the URL's value before it could be searched.
 *
 * `take` returns the value to put in the field only when the URL value is new: the first one seen
 * (page load) or one that differs from the last one seen (navigation to another search). The same
 * URL value seen again (a re-render, or the page adding `employeeId` to the URL) leaves the field
 * alone, and so does a URL without the parameter.
 */
export interface UrlSeed {
  take(urlValue: string | null): string | undefined;
}

export function createUrlSeed(): UrlSeed {
  let seen: string | null | undefined;
  return {
    take(urlValue) {
      if (urlValue === seen) return undefined;
      seen = urlValue;
      return urlValue ?? undefined;
    },
  };
}

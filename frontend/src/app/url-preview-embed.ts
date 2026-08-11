export const URL_PREVIEW_EMBED_SOURCE = 'url-preview-embed' as const;
export const URL_PREVIEW_NAVIGATION_TYPE = 'navigation' as const;

export interface UrlPreviewNavigation {
  path: string;
  kind: string;
  repository: string;
}

export interface UrlPreviewEmbedEnvironment {
  readonly href: string;
  replaceUrl(url: string): void;
  postToParent(message: UrlPreviewNavigationMessage, targetOrigin: '*'): void;
}

export interface UrlPreviewNavigationMessage {
  source: typeof URL_PREVIEW_EMBED_SOURCE;
  type: typeof URL_PREVIEW_NAVIGATION_TYPE;
  url: string;
}

/**
 * Pins the v1 preview boundary: navigation is reflected in the URL and, only
 * when embedded, reported to the parent. This contract carries no commands,
 * finding bodies, source text, credentials, or mutation requests.
 */
export function reportUrlPreviewNavigation(
  environment: UrlPreviewEmbedEnvironment,
  navigation: UrlPreviewNavigation,
  embedded: boolean,
): void {
  const url = new URL(environment.href);
  url.searchParams.set('path', navigation.path);
  url.searchParams.set('kind', navigation.kind);
  url.searchParams.set('repo', navigation.repository);
  const relativeUrl = `${url.pathname}?${url.searchParams}${url.hash}`;
  environment.replaceUrl(relativeUrl);
  if (!embedded) return;

  environment.postToParent({
    source: URL_PREVIEW_EMBED_SOURCE,
    type: URL_PREVIEW_NAVIGATION_TYPE,
    url: url.href,
  }, '*');
}

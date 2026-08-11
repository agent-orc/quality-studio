import { reportUrlPreviewNavigation, UrlPreviewNavigationMessage } from './url-preview-embed';

describe('url-preview-embed sender', () => {
  it('emits exactly the v1 navigation payload for path, kind, and repository changes while embedded', () => {
    const messages: Array<{ message: UrlPreviewNavigationMessage; targetOrigin: '*' }> = [];
    const replaced: string[] = [];

    reportUrlPreviewNavigation({
      href: 'https://quality.example.test/?theme=dark',
      replaceUrl: url => replaced.push(url),
      postToParent: (message, targetOrigin) => messages.push({ message, targetOrigin }),
    }, {
      path: 'src/Example.cs',
      kind: 'security',
      repository: 'repo-17',
    }, true);

    expect(replaced).toEqual(['/?theme=dark&path=src%2FExample.cs&kind=security&repo=repo-17']);
    expect(messages).toEqual([{
      message: {
        source: 'url-preview-embed',
        type: 'navigation',
        url: 'https://quality.example.test/?theme=dark&path=src%2FExample.cs&kind=security&repo=repo-17',
      },
      targetOrigin: '*',
    }]);
    expect(Object.keys(messages[0].message).sort()).toEqual(['source', 'type', 'url']);
  });

  it('updates the local URL without messaging the parent when not embedded', () => {
    const messages: UrlPreviewNavigationMessage[] = [];

    reportUrlPreviewNavigation({
      href: 'https://quality.example.test/',
      replaceUrl: () => undefined,
      postToParent: message => messages.push(message),
    }, { path: '.', kind: 'code', repository: 'default' }, false);

    expect(messages).toEqual([]);
  });
});

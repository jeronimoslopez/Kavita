import {sidecarUrlPattern} from './manage-media-settings.component';

describe('sidecar URL validation', () => {
  it.each(['', 'http://text:8000', 'https://sidecar.example.com/api'])('accepts %j', url => {
    expect(sidecarUrlPattern.test(url)).toBe(true);
  });

  it.each(['text:8000', 'ftp://text:8000', 'http://', 'http://text 8000', 'see http://text:8000'])('rejects %j', url => {
    expect(sidecarUrlPattern.test(url)).toBe(false);
  });
});

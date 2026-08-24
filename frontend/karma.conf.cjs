const { join } = require('node:path');

module.exports = function configureKarma(config) {
  config.set({
    frameworks: ['jasmine'],
    // karma-coverage has to be registered explicitly: this file replaces the builder's
    // default plugin list, so `ng test --code-coverage` would otherwise fail to load it.
    plugins: [require('karma-jasmine'), require('karma-chrome-launcher'), require('karma-coverage')],
    customLaunchers: {
      ChromeHeadlessNoSandbox: {
        base: 'ChromeHeadless',
        flags: ['--no-sandbox', '--disable-setuid-sandbox'],
      },
    },
    coverageReporter: {
      dir: join(__dirname, 'coverage'),
      subdir: '.',
      // lcov is what the repository's coverage ingestion and the ratchet both read.
      reporters: [{ type: 'lcovonly', file: 'lcov.info' }, { type: 'text-summary' }],
    },
  });
};

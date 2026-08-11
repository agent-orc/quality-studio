const { join } = require('node:path');

module.exports = function configureKarma(config) {
  config.set({
    frameworks: ['jasmine'],
    plugins: [require('karma-jasmine'), require('karma-chrome-launcher'), require('karma-coverage')],
    reporters: process.env.QUALITY_STUDIO_COVERAGE === '1' ? ['progress', 'coverage'] : ['progress'],
    coverageReporter: {
      dir: join(__dirname, 'coverage', 'frontend'),
      reporters: [{ type: 'lcovonly', subdir: '.' }],
    },
    customLaunchers: {
      ChromeHeadlessNoSandbox: {
        base: 'ChromeHeadless',
        flags: ['--no-sandbox', '--disable-setuid-sandbox'],
      },
    },
  });
};

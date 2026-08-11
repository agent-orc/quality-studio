module.exports = function configureKarma(config) {
  config.set({
    frameworks: ['jasmine'],
    plugins: [require('karma-jasmine'), require('karma-chrome-launcher'), require('karma-coverage')],
    coverageReporter: {
      dir: process.env.COVERAGE_DIR || 'coverage',
      reporters: [
        { type: 'lcovonly', subdir: '.' },
        { type: 'text-summary' },
      ],
    },
    customLaunchers: {
      ChromeHeadlessNoSandbox: {
        base: 'ChromeHeadless',
        flags: ['--no-sandbox', '--disable-setuid-sandbox'],
      },
    },
  });
};

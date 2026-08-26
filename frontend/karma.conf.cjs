const { resolve } = require('node:path');

module.exports = function configureKarma(config) {
  const coverageEnabled = process.argv.includes('--code-coverage');
  config.set({
    frameworks: ['jasmine'],
    plugins: [require('karma-jasmine'), require('karma-chrome-launcher'), require('karma-coverage')],
    reporters: coverageEnabled ? ['progress', 'coverage'] : ['progress'],
    coverageReporter: {
      dir: resolve(__dirname, 'coverage', 'frontend'),
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

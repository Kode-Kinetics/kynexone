const { withXcodeProject } = require('expo/config-plugins');

const securitySettings = {
  ENABLE_SECURITY_COMPILER_WARNINGS: 'YES',
  CLANG_ENABLE_STACK_ZERO_INIT: 'YES',
  GCC_WARN_ABOUT_RETURN_TYPE: 'YES_ERROR',
  GCC_WARN_UNINITIALIZED_AUTOS: 'YES_AGGRESSIVE',
  CLANG_ANALYZER_SECURITY_INSECUREAPI_RAND: 'YES',
  CLANG_ANALYZER_SECURITY_INSECUREAPI_STRCPY: 'YES',
};

module.exports = function withReleaseSecurity(config) {
  return withXcodeProject(config, (mod) => {
    const project = mod.modResults;
    const target = project.getFirstTarget().firstTarget;
    const configurations = project.pbxXCConfigurationList()[target.buildConfigurationList];
    for (const entry of configurations.buildConfigurations) {
      Object.assign(project.pbxXCBuildConfigurationSection()[entry.value].buildSettings, securitySettings);
    }
    return mod;
  });
};
module.exports.securitySettings = securitySettings;

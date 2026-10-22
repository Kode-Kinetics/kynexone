function applyInfoPlist(infoPlist) {
  return {
    ...infoPlist,
    CFBundleShortVersionString: '$(MARKETING_VERSION)',
    CFBundleVersion: '$(CURRENT_PROJECT_VERSION)',
  };
}

function applyEntitlements(entitlements) {
  return {
    ...entitlements,
    'aps-environment': '$(APS_ENVIRONMENT)',
  };
}

function applyBuildSettings(buildSettings, configurationName) {
  return {
    ...buildSettings,
    APS_ENVIRONMENT: configurationName === 'Release' ? 'production' : 'development',
  };
}

module.exports = function withIOSReleaseMetadata(config) {
  const { withEntitlementsPlist, withInfoPlist, withXcodeProject } = require('expo/config-plugins');
  config = withInfoPlist(config, (mod) => {
    mod.modResults = applyInfoPlist(mod.modResults);
    return mod;
  });
  config = withEntitlementsPlist(config, (mod) => {
    mod.modResults = applyEntitlements(mod.modResults);
    return mod;
  });
  return withXcodeProject(config, (mod) => {
    const project = mod.modResults;
    const target = project.getFirstTarget().firstTarget;
    const configurations = project.pbxXCConfigurationList()[target.buildConfigurationList];
    const section = project.pbxXCBuildConfigurationSection();
    for (const entry of configurations.buildConfigurations) {
      const configuration = section[entry.value];
      configuration.buildSettings = applyBuildSettings(configuration.buildSettings, configuration.name);
    }
    return mod;
  });
};

module.exports.applyInfoPlist = applyInfoPlist;
module.exports.applyEntitlements = applyEntitlements;
module.exports.applyBuildSettings = applyBuildSettings;

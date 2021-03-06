﻿using Microsoft.VisualStudio.TestTools.UnitTesting;
using PnP.Framework.Provisioning.Model;
using PnP.Framework.Test.Framework.Functional.Validators;

namespace PnP.Framework.Test.Framework.Functional.Implementation
{
    internal class RegionalSettingsImplementation : ImplementationBase
    {
        internal void SiteCollectionRegionalSettings(string url)
        {
            using (var cc = TestCommon.CreateClientContext(url))
            {
                var result = TestProvisioningTemplate(cc, "regionalsettings_add.xml", Handlers.RegionalSettings);
                RegionalSettingsValidator rv = new RegionalSettingsValidator();
                Assert.IsTrue(rv.Validate(result.SourceTemplate.RegionalSettings, result.TargetTemplate.RegionalSettings, result.TargetTokenParser));

                var result2 = TestProvisioningTemplate(cc, "regionalsettings_delta_1.xml", Handlers.RegionalSettings);
                RegionalSettingsValidator rv2 = new RegionalSettingsValidator();
                Assert.IsTrue(rv2.Validate(result2.SourceTemplate.RegionalSettings, result2.TargetTemplate.RegionalSettings, result2.TargetTokenParser));
            }
        }

        internal void WebRegionalSettings(string url)
        {
            using (var cc = TestCommon.CreateClientContext(url))
            {
                var result = TestProvisioningTemplate(cc, "regionalsettings_add.xml", Handlers.RegionalSettings);
                RegionalSettingsValidator rv = new RegionalSettingsValidator();
                Assert.IsTrue(rv.Validate(result.SourceTemplate.RegionalSettings, result.TargetTemplate.RegionalSettings, result.TargetTokenParser));

                var result2 = TestProvisioningTemplate(cc, "regionalsettings_delta_1.xml", Handlers.RegionalSettings);
                RegionalSettingsValidator rv2 = new RegionalSettingsValidator();
                Assert.IsTrue(rv2.Validate(result2.SourceTemplate.RegionalSettings, result2.TargetTemplate.RegionalSettings, result2.TargetTokenParser));
            }
        }
    }
}
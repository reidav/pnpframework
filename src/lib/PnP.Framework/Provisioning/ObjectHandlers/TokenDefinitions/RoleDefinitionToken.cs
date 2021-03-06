using Microsoft.SharePoint.Client;
using PnP.Framework.Attributes;

namespace PnP.Framework.Provisioning.ObjectHandlers.TokenDefinitions
{
    [TokenDefinitionDescription(
        Token = "{roledefinition:[roletype]}",
        Description = "Returns the name of role definition given the role type",
        Example = "{roledefinition:Editor}",
        Returns = "Editors")]
    internal class RoleDefinitionToken : TokenDefinition
    {
        private readonly string name;
        public RoleDefinitionToken(Web web, RoleDefinition definition)
            : base(web, $"{{roledefinition:{definition.RoleTypeKind}}}")
        {
            name = definition.EnsureProperty(r => r.Name);

        }

        public override string GetReplaceValue()
        {
            if (string.IsNullOrEmpty(CacheValue))
            {
                CacheValue = name;
            }
            return CacheValue;
        }
    }
}
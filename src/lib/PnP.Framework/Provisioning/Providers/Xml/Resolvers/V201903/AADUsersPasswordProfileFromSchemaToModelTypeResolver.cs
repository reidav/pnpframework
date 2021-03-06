﻿using PnP.Framework.Extensions;
using System;
using System.Collections.Generic;
using AAD = PnP.Framework.Provisioning.Model.AzureActiveDirectory;

namespace PnP.Framework.Provisioning.Providers.Xml.Resolvers
{
    /// <summary>
    /// Resolves the AAD Users from the Schema to the Model
    /// </summary>
    internal class AADUsersPasswordProfileFromSchemaToModelTypeResolver : ITypeResolver
    {
        public string Name => this.GetType().Name;

        public bool CustomCollectionResolver => false;

        public object Resolve(object source, Dictionary<String, IResolver> resolvers = null, Boolean recursive = false)
        {
            var result = new AAD.PasswordProfile();

            var passwordProfile = source.GetPublicInstancePropertyValue("PasswordProfile");

            if (null != passwordProfile)
            {
                PnPObjectsMapper.MapProperties(passwordProfile, result, resolvers, recursive);
            }

            return (result);
        }
    }
}

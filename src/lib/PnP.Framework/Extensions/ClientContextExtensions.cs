using PnP.Framework;
using PnP.Framework.Diagnostics;
using PnP.Framework.Provisioning.ObjectHandlers;
using PnP.Framework.Sites;
using PnP.Framework.Utilities;
using PnP.Framework.Utilities.Async;
using PnP.Framework.Utilities.Context;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Microsoft.SharePoint.Client
{
    /// <summary>
    /// Class that deals with cloning client context object, getting access token and validates server version
    /// </summary>
    public static partial class ClientContextExtensions
    {
        private static readonly string userAgentFromConfig = null;
        private static string accessToken = null;
        //private static bool hasAuthCookies;

        /// <summary>
        /// Static constructor, only executed once per class load
        /// </summary>
#pragma warning disable CA1810
        static ClientContextExtensions()
        {
            try
            {
                ClientContextExtensions.userAgentFromConfig = ConfigurationManager.AppSettings["SharePointPnPUserAgent"];
            }
            catch // throws exception if being called from a .NET Standard 2.0 application
            {

            }
            if (string.IsNullOrWhiteSpace(ClientContextExtensions.userAgentFromConfig))
            {
                ClientContextExtensions.userAgentFromConfig = Environment.GetEnvironmentVariable("SharePointPnPUserAgent", EnvironmentVariableTarget.Process);
            }
        }
#pragma warning restore CA1810
        /// <summary>
        /// Clones a ClientContext object while "taking over" the security context of the existing ClientContext instance
        /// </summary>
        /// <param name="clientContext">ClientContext to be cloned</param>
        /// <param name="siteUrl">Site URL to be used for cloned ClientContext</param>
        /// <param name="accessTokens">Dictionary of access tokens for sites URLs</param>
        /// <returns>A ClientContext object created for the passed site URL</returns>
        public static ClientContext Clone(this ClientRuntimeContext clientContext, string siteUrl, Dictionary<string, string> accessTokens = null)
        {
            if (string.IsNullOrWhiteSpace(siteUrl))
            {
                throw new ArgumentException(CoreResources.ClientContextExtensions_Clone_Url_of_the_site_is_required_, nameof(siteUrl));
            }

            return clientContext.Clone(new Uri(siteUrl), accessTokens);
        }

        /// <summary>
        /// Executes the current set of data retrieval queries and method invocations and retries it if needed using the Task Library.
        /// </summary>
        /// <param name="clientContext">clientContext to operate on</param>
        /// <param name="retryCount">Number of times to retry the request</param>
        /// <param name="delay">Milliseconds to wait before retrying the request. The delay will be increased (doubled) every retry</param>
        /// <param name="userAgent">UserAgent string value to insert for this request. You can define this value in your app's config file using key="SharePointPnPUserAgent" value="PnPRocks"></param>
        public static Task ExecuteQueryRetryAsync(this ClientRuntimeContext clientContext, int retryCount = 10, int delay = 500, string userAgent = null)
        {
            return ExecuteQueryImplementation(clientContext, retryCount, delay, userAgent);
        }

        /// <summary>
        /// Executes the current set of data retrieval queries and method invocations and retries it if needed.
        /// </summary>
        /// <param name="clientContext">clientContext to operate on</param>
        /// <param name="retryCount">Number of times to retry the request</param>
        /// <param name="delay">Milliseconds to wait before retrying the request. The delay will be increased (doubled) every retry</param>
        /// <param name="userAgent">UserAgent string value to insert for this request. You can define this value in your app's config file using key="SharePointPnPUserAgent" value="PnPRocks"></param>
        public static void ExecuteQueryRetry(this ClientRuntimeContext clientContext, int retryCount = 10, int delay = 500, string userAgent = null)
        {
            Task.Run(() => ExecuteQueryImplementation(clientContext, retryCount, delay, userAgent)).GetAwaiter().GetResult();
        }

        private static async Task ExecuteQueryImplementation(ClientRuntimeContext clientContext, int retryCount = 10, int delay = 500, string userAgent = null)
        {

            await new SynchronizationContextRemover();

            // Set the TLS preference. Needed on some server os's to work when Office 365 removes support for TLS 1.0
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

            var clientTag = string.Empty;
            if (clientContext is PnPClientContext)
            {
                retryCount = (clientContext as PnPClientContext).RetryCount;
                delay = (clientContext as PnPClientContext).Delay;
                clientTag = (clientContext as PnPClientContext).ClientTag;
            }

            int retryAttempts = 0;
            int backoffInterval = delay;
            int retryAfterInterval = 0;
            bool retry = false;
            ClientRequestWrapper wrapper = null;

            if (retryCount <= 0)
                throw new ArgumentException("Provide a retry count greater than zero.");

            if (delay <= 0)
                throw new ArgumentException("Provide a delay greater than zero.");

            // Do while retry attempt is less than retry count
            while (retryAttempts < retryCount)
            {
                try
                {
                    clientContext.ClientTag = SetClientTag(clientTag);

                    // Make CSOM request more reliable by disabling the return value cache. Given we 
                    // often clone context objects and the default value is
                    clientContext.DisableReturnValueCache = true;
                    // Add event handler to "insert" app decoration header to mark the PnP Sites Core library as a known application
                    EventHandler<WebRequestEventArgs> appDecorationHandler = AttachRequestUserAgent(userAgent);

                    clientContext.ExecutingWebRequest += appDecorationHandler;

                    // DO NOT CHANGE THIS TO EXECUTEQUERYRETRY
                    if (!retry)
                    {
                        await clientContext.ExecuteQueryAsync();
                    }
                    else
                    {
                        if (wrapper != null && wrapper.Value != null)
                        {
                            await clientContext.RetryQueryAsync(wrapper.Value);
                        }
                    }

                    // Remove the app decoration event handler after the executequery
                    clientContext.ExecutingWebRequest -= appDecorationHandler;

                    return;
                }
                catch (WebException wex)
                {
                    var response = wex.Response as HttpWebResponse;
                    // Check if request was throttled - http status code 429
                    // Check is request failed due to server unavailable - http status code 503
                    if (response != null &&
                        (response.StatusCode == (HttpStatusCode)429
                        || response.StatusCode == (HttpStatusCode)503
                        // || response.StatusCode == (HttpStatusCode)500
                        ))
                    {
                        Log.Warning(Constants.LOGGING_SOURCE, CoreResources.ClientContextExtensions_ExecuteQueryRetry, backoffInterval);

                        wrapper = (ClientRequestWrapper)wex.Data["ClientRequest"];
                        retry = true;
                        retryAfterInterval = backoffInterval;

                        //Add delay for retry, retry-after header is specified in seconds
                        await Task.Delay(retryAfterInterval);

                        //Add to retry count and increase delay.
                        retryAttempts++;
                        backoffInterval = backoffInterval * 2;
                    }
                    else
                    {
                        var errorSb = new System.Text.StringBuilder();

                        errorSb.AppendLine(wex.ToString());

                        if (response != null)
                        {
                            //if(response.Headers["SPRequestGuid"] != null) 
                            if (response.Headers.AllKeys.Any(k => string.Equals(k, "SPRequestGuid", StringComparison.InvariantCultureIgnoreCase)))
                            {
                                var spRequestGuid = response.Headers["SPRequestGuid"];
                                errorSb.AppendLine($"ServerErrorTraceCorrelationId: {spRequestGuid}");
                            }
                        }

                        Log.Error(Constants.LOGGING_SOURCE, CoreResources.ClientContextExtensions_ExecuteQueryRetryException, errorSb.ToString());
                        throw;
                    }
                }
                catch (Microsoft.SharePoint.Client.ServerException serverEx)
                {
                    var errorSb = new System.Text.StringBuilder();

                    errorSb.AppendLine(serverEx.ToString());
                    errorSb.AppendLine($"ServerErrorCode: {serverEx.ServerErrorCode}");
                    errorSb.AppendLine($"ServerErrorTypeName: {serverEx.ServerErrorTypeName}");
                    errorSb.AppendLine($"ServerErrorTraceCorrelationId: {serverEx.ServerErrorTraceCorrelationId}");
                    errorSb.AppendLine($"ServerErrorValue: {serverEx.ServerErrorValue}");
                    errorSb.AppendLine($"ServerErrorDetails: {serverEx.ServerErrorDetails}");

                    Log.Error(Constants.LOGGING_SOURCE, CoreResources.ClientContextExtensions_ExecuteQueryRetryException, errorSb.ToString());

                    throw;
                }
            }

            throw new MaximumRetryAttemptedException($"Maximum retry attempts {retryCount}, has be attempted.");
        }

        /// <summary>
        /// Attaches either a passed user agent, or one defined in the App.config file, to the WebRequstExecutor UserAgent property.
        /// </summary>
        /// <param name="customUserAgent">a custom user agent to override any defined in App.config</param>
        /// <returns>An EventHandler of WebRequestEventArgs.</returns>
        private static EventHandler<WebRequestEventArgs> AttachRequestUserAgent(string customUserAgent)
        {
            return (s, e) =>
            {
                bool overrideUserAgent = true;
                var existingUserAgent = e.WebRequestExecutor.WebRequest.UserAgent;
                if (!string.IsNullOrEmpty(existingUserAgent) && existingUserAgent.StartsWith("NONISV|SharePointPnP|PnPPS/"))
                {
                    overrideUserAgent = false;
                }
                if (overrideUserAgent)
                {
                    if (string.IsNullOrEmpty(customUserAgent) && !string.IsNullOrEmpty(ClientContextExtensions.userAgentFromConfig))
                    {
                        customUserAgent = userAgentFromConfig;
                    }
                    e.WebRequestExecutor.WebRequest.UserAgent = string.IsNullOrEmpty(customUserAgent) ? $"{PnPCoreUtilities.PnPCoreUserAgent}" : customUserAgent;
                }
            };
        }

        /// <summary>
        /// Sets the client context client tag on outgoing CSOM requests.
        /// </summary>
        /// <param name="clientTag">An optional client tag to set on client context requests.</param>
        /// <returns></returns>
        private static string SetClientTag(string clientTag = "")
        {
            // ClientTag property is limited to 32 chars
            if (string.IsNullOrEmpty(clientTag))
            {
                clientTag = $"{PnPCoreUtilities.PnPCoreVersionTag}:{GetCallingPnPMethod()}";
            }
            if (clientTag.Length > 32)
            {
                clientTag = clientTag.Substring(0, 32);
            }

            return clientTag;
        }

        /// <summary>
        /// Clones a ClientContext object while "taking over" the security context of the existing ClientContext instance
        /// </summary>
        /// <param name="clientContext">ClientContext to be cloned</param>
        /// <param name="siteUrl">Site URL to be used for cloned ClientContext</param>
        /// <param name="accessTokens">Dictionary of access tokens for sites URLs</param>
        /// <returns>A ClientContext object created for the passed site URL</returns>
        public static ClientContext Clone(this ClientRuntimeContext clientContext, Uri siteUrl, Dictionary<string, string> accessTokens = null)
        {
            return Clone(clientContext, new ClientContext(siteUrl), siteUrl, accessTokens);
        }
        /// <summary>
        /// Clones a ClientContext object while "taking over" the security context of the existing ClientContext instance
        /// </summary>
        /// <param name="clientContext">ClientContext to be cloned</param>
        /// <param name="targetContext">CientContext stub to be used for cloning</param>
        /// <param name="siteUrl">Site URL to be used for cloned ClientContext</param>
        /// <param name="accessTokens">Dictionary of access tokens for sites URLs</param>
        /// <returns>A ClientContext object created for the passed site URL</returns>
        internal static ClientContext Clone(this ClientRuntimeContext clientContext, ClientContext targetContext, Uri siteUrl, Dictionary<string, string> accessTokens = null)
        {
            if (siteUrl == null)
            {
                throw new ArgumentException(CoreResources.ClientContextExtensions_Clone_Url_of_the_site_is_required_, nameof(siteUrl));
            }

            ClientContext clonedClientContext = targetContext;
            clonedClientContext.ClientTag = clientContext.ClientTag;
            clonedClientContext.DisableReturnValueCache = clientContext.DisableReturnValueCache;


            // Check if we do have context settings
            var contextSettings = clientContext.GetContextSettings();

            if (contextSettings != null) // We do have more information about this client context, so let's use it to do a more intelligent clone
            {
                string newSiteUrl = siteUrl.ToString();

                // A diffent host = different audience ==> new access token is needed
                if (contextSettings.UsesDifferentAudience(newSiteUrl))
                {

                    var authManager = contextSettings.AuthenticationManager;
                    ClientContext newClientContext = null;
                    if (contextSettings.Type != ClientContextType.Cookie)
                    {
                        if (contextSettings.Type == ClientContextType.SharePointACSAppOnly)
                        {
                            newClientContext = authManager.GetACSAppOnlyContext(newSiteUrl, contextSettings.ClientId, contextSettings.ClientSecret, contextSettings.Environment);
                        }
                        else
                        {
                            newClientContext = authManager.GetContextAsync(newSiteUrl).GetAwaiter().GetResult();
                        }
                    }
                    else
                    {
                        newClientContext = new ClientContext(newSiteUrl);
                        newClientContext.ExecutingWebRequest += (sender, webRequestEventArgs) =>
                        {
                            // Call the ExecutingWebRequest delegate method from the original ClientContext object, but pass along the webRequestEventArgs of 
                            // the new delegate method
                            MethodInfo methodInfo = clientContext.GetType().GetMethod("OnExecutingWebRequest", BindingFlags.Instance | BindingFlags.NonPublic);
                            object[] parametersArray = new object[] { webRequestEventArgs };
                            methodInfo.Invoke(clientContext, parametersArray);
                        };
                        ClientContextSettings clientContextSettings = new ClientContextSettings()
                        {
                            Type = ClientContextType.Cookie,
                            SiteUrl = newSiteUrl
                        };

                        newClientContext.AddContextSettings(clientContextSettings);
                    }
                    if (newClientContext != null)
                    {
                        //Take over the form digest handling setting
                        newClientContext.ClientTag = clientContext.ClientTag;
                        newClientContext.DisableReturnValueCache = clientContext.DisableReturnValueCache;
                        return newClientContext;
                    }
                    else
                    {
                        throw new Exception($"Cloning for context setting type {contextSettings.Type} was not yet implemented");
                    }
                }
                else
                {
                    // Take over the context settings, this is needed if we later on want to clone this context to a different audience
                    contextSettings.SiteUrl = newSiteUrl;
                    clonedClientContext.AddContextSettings(contextSettings);

                    clonedClientContext.ExecutingWebRequest += delegate (object oSender, WebRequestEventArgs webRequestEventArgs)
                    {
                        // Call the ExecutingWebRequest delegate method from the original ClientContext object, but pass along the webRequestEventArgs of 
                        // the new delegate method
                        MethodInfo methodInfo = clientContext.GetType().GetMethod("OnExecutingWebRequest", BindingFlags.Instance | BindingFlags.NonPublic);
                        object[] parametersArray = new object[] { webRequestEventArgs };
                        methodInfo.Invoke(clientContext, parametersArray);
                    };
                }
            }
            else // Fallback the default cloning logic if there were not context settings available
            {
                //Take over the form digest handling setting

                var originalUri = new Uri(clientContext.Url);
                // If the cloned host is not the same as the original one
                // and if there is an active PnPProvisioningContext
                if (originalUri.Host != siteUrl.Host &&
                    PnPProvisioningContext.Current != null)
                {
                    // Let's apply that specific Access Token
                    clonedClientContext.ExecutingWebRequest += (sender, args) =>
                    {
                        // We get a fresh new Access Token for every request, to avoid using an expired one
                        var accessToken = PnPProvisioningContext.Current.AcquireToken(siteUrl.Authority, null);
                        args.WebRequestExecutor.RequestHeaders["Authorization"] = "Bearer " + accessToken;
                    };
                }
                // Else if the cloned host is not the same as the original one
                // and if there is a custom Access Token for it in the input arguments
                else if (originalUri.Host != siteUrl.Host &&
                    accessTokens != null && accessTokens.Count > 0 &&
                    accessTokens.ContainsKey(siteUrl.Authority))
                {
                    // Let's apply that specific Access Token
                    clonedClientContext.ExecutingWebRequest += (sender, args) =>
                    {
                        args.WebRequestExecutor.RequestHeaders["Authorization"] = "Bearer " + accessTokens[siteUrl.Authority];
                    };
                }
                // Else if the cloned host is not the same as the original one
                // and if the client context is a PnPClientContext with custom access tokens in its property bag
                else if (originalUri.Host != siteUrl.Host &&
                    accessTokens == null && clientContext is PnPClientContext &&
                    ((PnPClientContext)clientContext).PropertyBag.ContainsKey("AccessTokens") &&
                    ((Dictionary<string, string>)((PnPClientContext)clientContext).PropertyBag["AccessTokens"]).ContainsKey(siteUrl.Authority))
                {
                    // Let's apply that specific Access Token
                    clonedClientContext.ExecutingWebRequest += (sender, args) =>
                    {
                        args.WebRequestExecutor.RequestHeaders["Authorization"] = "Bearer " + ((Dictionary<string, string>)((PnPClientContext)clientContext).PropertyBag["AccessTokens"])[siteUrl.Authority];
                    };
                }
                else
                {
                    // In case of app only or SAML
                    clonedClientContext.ExecutingWebRequest += (sender, webRequestEventArgs) =>
                    {
                        // Call the ExecutingWebRequest delegate method from the original ClientContext object, but pass along the webRequestEventArgs of 
                        // the new delegate method
                        MethodInfo methodInfo = clientContext.GetType().GetMethod("OnExecutingWebRequest", BindingFlags.Instance | BindingFlags.NonPublic);
                        object[] parametersArray = new object[] { webRequestEventArgs };
                        methodInfo.Invoke(clientContext, parametersArray);
                    };
                }
            }

            return clonedClientContext;
        }

        /// <summary>
        /// Returns the number of pending requests
        /// </summary>
        /// <param name="clientContext">Client context to check the pending requests for</param>
        /// <returns>The number of pending requests</returns>
        public static int PendingRequestCount(this ClientRuntimeContext clientContext)
        {
            int count = 0;

            if (clientContext.HasPendingRequest)
            {
                var result = clientContext.PendingRequest.GetType().GetProperty("Actions", BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.NonPublic);
                if (result != null)
                {
                    var propValue = result.GetValue(clientContext.PendingRequest);
                    if (propValue != null)
                    {
                        count = (propValue as System.Collections.Generic.List<ClientAction>).Count;
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// Gets a site collection context for the passed web. This site collection client context uses the same credentials
        /// as the passed client context
        /// </summary>
        /// <param name="clientContext">Client context to take the credentials from</param>
        /// <returns>A site collection client context object for the site collection</returns>
        public static ClientContext GetSiteCollectionContext(this ClientRuntimeContext clientContext)
        {
            Site site = (clientContext as ClientContext).Site;
            if (!site.IsObjectPropertyInstantiated("Url"))
            {
                clientContext.Load(site);
                clientContext.ExecuteQueryRetry();
            }
            return clientContext.Clone(site.Url);
        }

        /// <summary>
        /// Checks if the used ClientContext is app-only
        /// </summary>
        /// <param name="clientContext">The ClientContext to inspect</param>
        /// <returns>True if app-only, false otherwise</returns>
        public static bool IsAppOnly(this ClientRuntimeContext clientContext)
        {
            // Set initial result to false
            var result = false;

            // Try to get an access token from the current context
            var accessToken = clientContext.GetAccessToken();

            // If any
            if (!String.IsNullOrEmpty(accessToken))
            {
                // Try to decode the access token
                var token = new JwtSecurityToken(accessToken);

                // Search for the UPN claim, to see if we have user's delegation
                var upn = token.Claims.FirstOrDefault(claim => claim.Type == "upn")?.Value;
                if (String.IsNullOrEmpty(upn))
                {
                    result = true;
                }
            }
            else if (clientContext.Credentials == null)
            {
                result = true;
            }

            return result;
        }


        /// <summary>
        /// Gets an access token from a <see cref="ClientContext"/> instance. Only works when using an add-in or app-only authentication flow.
        /// </summary>
        /// <param name="clientContext"><see cref="ClientContext"/> instance to obtain an access token for</param>
        /// <returns>Access token for the given <see cref="ClientContext"/> instance</returns>
        public static string GetAccessToken(this ClientRuntimeContext clientContext)
        {
            string accessToken = null;

            if (PnPProvisioningContext.Current?.AcquireTokenAsync != null)
            {
                accessToken = PnPProvisioningContext.Current.AcquireToken(new Uri(clientContext.Url).Authority, null);
            }
            else
            {
                if (clientContext.GetContextSettings()?.AuthenticationManager != null)
                {
                    var contextSettings = clientContext.GetContextSettings();
                    accessToken = contextSettings.AuthenticationManager.GetAccessTokenAsync(clientContext.Url).GetAwaiter().GetResult();
                }
                else
                {
                    EventHandler<WebRequestEventArgs> handler = (s, e) =>
                    {
                        string authorization = e.WebRequestExecutor.RequestHeaders["Authorization"];
                        if (!string.IsNullOrEmpty(authorization))
                        {
                            accessToken = authorization.Replace("Bearer ", string.Empty);
                        }
                    };
                    // Issue a dummy request to get it from the Authorization header
                    clientContext.ExecutingWebRequest += handler;
                    clientContext.ExecuteQuery();
                    clientContext.ExecutingWebRequest -= handler;
                }
            }

            return accessToken;
        }

#pragma warning disable CA1034,CA2229,CA1032
        /// <summary>
        /// Defines a Maximum Retry Attemped Exception
        /// </summary>
        [Serializable]
        public class MaximumRetryAttemptedException : Exception
        {
            /// <summary>
            /// Constructor
            /// </summary>
            /// <param name="message"></param>
            public MaximumRetryAttemptedException(string message)
                : base(message)
            {

            }
        }
#pragma warning restore CA1034,CA2229,CA1032

        /// <summary>
        /// Checks the server library version of the context for a minimally required version
        /// </summary>
        /// <param name="clientContext">clientContext to operate on</param>
        /// <param name="minimallyRequiredVersion">provide version to validate</param>
        /// <returns>True if it has minimal required version, false otherwise</returns>
        public static bool HasMinimalServerLibraryVersion(this ClientRuntimeContext clientContext, string minimallyRequiredVersion)
        {
            return HasMinimalServerLibraryVersion(clientContext, new Version(minimallyRequiredVersion));
        }

        /// <summary>
        /// Checks the server library version of the context for a minimally required version
        /// </summary>
        /// <param name="clientContext">clientContext to operate on</param>
        /// <param name="minimallyRequiredVersion">provide version to validate</param>
        /// <returns>True if it has minimal required version, false otherwise</returns>
        public static bool HasMinimalServerLibraryVersion(this ClientRuntimeContext clientContext, Version minimallyRequiredVersion)
        {
            bool hasMinimalVersion = false;
            try
            {
                clientContext.ExecuteQueryRetry();
                hasMinimalVersion = clientContext.ServerLibraryVersion.CompareTo(minimallyRequiredVersion) >= 0;
            }
            catch (PropertyOrFieldNotInitializedException)
            {
                // swallow the exception.
            }

            return hasMinimalVersion;
        }

        /// <summary>
        /// Returns the name of the method calling ExecuteQueryRetry and ExecuteQueryRetryAsync
        /// </summary>
        /// <returns>A string with the method name</returns>
        private static string GetCallingPnPMethod()
        {
            StackTrace t = new StackTrace();

            string pnpMethod = "";
            try
            {
                for (int i = 0; i < t.FrameCount; i++)
                {
                    var frame = t.GetFrame(i);
                    var frameName = frame.GetMethod().Name;
                    if (frameName.Equals("ExecuteQueryRetry") || frameName.Equals("ExecuteQueryRetryAsync"))
                    {
                        var method = t.GetFrame(i + 1).GetMethod();

                        // Only return the calling method in case ExecuteQueryRetry was called from inside the PnP core library
                        if (method.Module.Name.Equals("PnP.Framework.dll", StringComparison.InvariantCultureIgnoreCase))
                        {
                            pnpMethod = method.Name;
                        }
                        break;
                    }
                }
            }
            catch
            {
                // ignored
            }

            return pnpMethod;
        }


        /// <summary>
        /// Returns the request digest from the current session/site
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        public static async Task<string> GetRequestDigestAsync(this ClientContext context)
        {
            await new SynchronizationContextRemover();

            //InitializeSecurity(context);

            using (var handler = new HttpClientHandler())
            {
                string responseString = string.Empty;
                var accessToken = context.GetAccessToken();

                context.Web.EnsureProperty(w => w.Url);

                using (var httpClient = new PnPHttpProvider(handler))
                {
                    string requestUrl = String.Format("{0}/_api/contextinfo", context.Web.Url);
                    using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
                    {
                        request.Headers.Add("accept", "application/json;odata=verbose");
                        if (!string.IsNullOrEmpty(accessToken))
                        {
                            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                        }
                        else
                        {
                            if (context.Credentials is NetworkCredential networkCredential)
                            {
                                handler.Credentials = networkCredential;
                            }
                        }

                        HttpResponseMessage response = await httpClient.SendAsync(request);

                        if (response.IsSuccessStatusCode)
                        {
                            responseString = await response.Content.ReadAsStringAsync();
                        }
                        else
                        {
                            var errorSb = new System.Text.StringBuilder();

                            errorSb.AppendLine(await response.Content.ReadAsStringAsync());
                            if (response.Headers.Contains("SPRequestGuid"))
                            {
                                var values = response.Headers.GetValues("SPRequestGuid");
                                if (values != null)
                                {
                                    var spRequestGuid = values.FirstOrDefault();
                                    errorSb.AppendLine($"ServerErrorTraceCorrelationId: {spRequestGuid}");
                                }
                            }

                            throw new Exception(errorSb.ToString());
                        }
                    }
                }
                var contextInformation = JsonSerializer.Deserialize<JsonElement>(responseString);

                string formDigestValue = contextInformation.GetProperty("d").GetProperty("GetContextWebInformation").GetProperty("FormDigestValue").GetString();
                return await Task.Run(() => formDigestValue).ConfigureAwait(false);
            }
        }

        private static void Context_ExecutingWebRequest(object sender, WebRequestEventArgs e)
        {
            if (!String.IsNullOrEmpty(e.WebRequestExecutor.RequestHeaders.Get("Authorization")))
            {
                accessToken = e.WebRequestExecutor.RequestHeaders.Get("Authorization").Replace("Bearer ", "");
            }
        }

        /// <summary>
        /// BETA: Creates a Communication Site Collection
        /// </summary>
        /// <param name="clientContext"></param>
        /// <param name="siteCollectionCreationInformation"></param>
        /// <returns></returns>
        public static async Task<ClientContext> CreateSiteAsync(this ClientContext clientContext, CommunicationSiteCollectionCreationInformation siteCollectionCreationInformation)
        {
            await new SynchronizationContextRemover();

            return await SiteCollection.CreateAsync(clientContext, siteCollectionCreationInformation);
        }

        /// <summary>
        /// BETA: Creates a Team Site Collection with no group
        /// </summary>
        /// <param name="clientContext"></param>
        /// <param name="siteCollectionCreationInformation"></param>
        /// <returns></returns>
        public static async Task<ClientContext> CreateSiteAsync(this ClientContext clientContext, TeamNoGroupSiteCollectionCreationInformation siteCollectionCreationInformation)
        {
            await new SynchronizationContextRemover();

            return await SiteCollection.CreateAsync(clientContext, siteCollectionCreationInformation);
        }

        /// <summary>
        /// BETA: Creates a Team Site Collection
        /// </summary>
        /// <param name="clientContext"></param>
        /// <param name="siteCollectionCreationInformation"></param>
        /// <returns></returns>
        public static async Task<ClientContext> CreateSiteAsync(this ClientContext clientContext, TeamSiteCollectionCreationInformation siteCollectionCreationInformation)
        {
            await new SynchronizationContextRemover();

            return await SiteCollection.CreateAsync(clientContext, siteCollectionCreationInformation);
        }

        /// <summary>
        /// BETA: Groupifies a classic Team Site Collection
        /// </summary>
        /// <param name="clientContext">ClientContext instance of the site to be groupified</param>
        /// <param name="siteCollectionGroupifyInformation">Information needed to groupify this site</param>
        /// <returns>The clientcontext of the groupified site</returns>
        public static async Task<ClientContext> GroupifySiteAsync(this ClientContext clientContext, TeamSiteCollectionGroupifyInformation siteCollectionGroupifyInformation)
        {
            await new SynchronizationContextRemover();

            return await SiteCollection.GroupifyAsync(clientContext, siteCollectionGroupifyInformation);
        }

        /// <summary>
        /// Checks if an alias is already used for an office 365 group or not
        /// </summary>
        /// <param name="clientContext">ClientContext of the site to operate against</param>
        /// <param name="alias">Alias to verify</param>
        /// <returns>True if in use, false otherwise</returns>
        public static async Task<bool> AliasExistsAsync(this ClientContext clientContext, string alias)
        {
            await new SynchronizationContextRemover();

            return await SiteCollection.AliasExistsAsync(clientContext, alias);
        }

        /// <summary>
        /// Enable MS Teams team on a group connected team site
        /// </summary>
        /// <param name="clientContext"></param>
        /// <returns></returns>
        public static async Task<string> TeamifyAsync(this ClientContext clientContext)
        {
            await new SynchronizationContextRemover();

            return await SiteCollection.TeamifySiteAsync(clientContext);
        }


        /// <summary>
        /// Checks whether the teamify prompt is hidden in O365 Group connected sites
        /// </summary>
        /// <param name="clientContext">ClientContext of the site to operate against</param>
        /// <returns></returns>
        public static async Task<bool> IsTeamifyPromptHiddenAsync(this ClientContext clientContext)
        {
            await new SynchronizationContextRemover();

            return await SiteCollection.IsTeamifyPromptHiddenAsync(clientContext);
        }

        [Obsolete("Use IsTeamifyPromptHiddenAsync")]
        public static async Task<bool> IsTeamifyPromptHidden(this ClientContext clientContext)
        {
            return await IsTeamifyPromptHiddenAsync(clientContext);
        }

        /// <summary>
        /// Hide the teamify prompt displayed in O365 group connected sites
        /// </summary>
        /// <param name="clientContext">ClientContext of the site to operate against</param>
        /// <returns></returns>
        public static async Task<bool> HideTeamifyPromptAsync(this ClientContext clientContext)
        {
            await new SynchronizationContextRemover();
            return await SiteCollection.HideTeamifyPromptAsync(clientContext);
        }

        /// <summary>
        /// Deletes a Communication site or a group-less Modern team site
        /// </summary>
        /// <param name="clientContext"></param>
        /// <returns></returns>
        public static async Task<bool> DeleteSiteAsync(this ClientContext clientContext)
        {
            await new SynchronizationContextRemover();

            return await SiteCollection.DeleteSiteAsync(clientContext);
        }
    }
}

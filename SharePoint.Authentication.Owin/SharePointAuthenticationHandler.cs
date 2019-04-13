﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http.Dependencies;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Owin;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Infrastructure;
using Newtonsoft.Json;
using SharePoint.Authentication.Caching;
using SharePoint.Authentication.Exceptions;
using SharePoint.Authentication.Owin.Models;

namespace SharePoint.Authentication.Owin
{
    public class SharePointAuthenticationHandler : AuthenticationHandler<SharePointAuthenticationOptions>
    {
        private readonly SharePointAuthenticationOptions _sharePointAuthenticationOptions;

        public SharePointAuthenticationHandler(SharePointAuthenticationOptions sharePointAuthenticationOptions)
        {
            _sharePointAuthenticationOptions = sharePointAuthenticationOptions;
        }

        protected override async Task<AuthenticationTicket> AuthenticateCoreAsync()
        {
            var handler = new JwtSecurityTokenHandler();

            try
            {
                using (var dependencyScope = _sharePointAuthenticationOptions.DependencyResolver.BeginScope())
                {
                    var owin = HttpContext.Current.GetOwinContext();

                    if (owin.Request.Method == HttpMethod.Options.Method)
                        return new AuthenticationTicket(new ClaimsIdentity(), new AuthenticationProperties());

                    if (_sharePointAuthenticationOptions.AllowNonBrowserRequests == false && (!owin.Request.Headers.ContainsKey("User-Agent") ||
                         owin.Request.Headers["User-Agent"].ToLower().StartsWith("curl") != false) &&
                        !owin.Request.Headers.ContainsKey("Origin") && !owin.Request.Headers.ContainsKey("Referer"))
                    {
                        throw new SharePointAuthenticationException($"Request wasn't sent from a browser. " +
                                                 $"User-Agent: {(owin.Request.Headers.ContainsKey("User-Agent") ? owin.Request.Headers["User-Agent"] : null)}, " +
                                                 $"Origin: {(owin.Request.Headers.ContainsKey("Origin") ? owin.Request.Headers["Origin"] : null)}, " +
                                                 $"Referer: {(owin.Request.Headers.ContainsKey("Referer") ? owin.Request.Headers["Referer"] : null)}, ");
                    }

                    var token = await GetAccessToken(dependencyScope, owin);

                    if (token == null)
                    {
                        throw new SharePointAuthenticationException("Context token is null");
                    }
                    
                    var tokenValidationParameters = new TokenValidationParameters
                    {
                        //// The signing key must match!
                        ValidateIssuerSigningKey = false,
                        //IssuerSigningKey = new InMemorySymmetricSecurityKey(secret),

                        //// Validate the JWT Issuer (iss) claim
                        ValidateIssuer = false,
                        //ValidIssuer = issuer,

                        //// Validate the JWT Audience (aud) claim
                        ValidateAudience = false,
                        //ValidAudience = audience,

                        // Validate the token expiry
                        ValidateLifetime = true,

                        // If you want to allow a certain amount of clock drift, set that here:
                        ClockSkew = TimeSpan.Zero,

                        RequireSignedTokens = false,
                
                        IssuerSigningKeys = GetPublicKeysCached(dependencyScope)
                    };

                    var principal = handler.ValidateToken(token, tokenValidationParameters, out var validToken);

                    if (!(validToken is JwtSecurityToken))
                    {
                        throw new SharePointAuthenticationException("Invalid JWT");
                    }

                    if (!(principal.Identity is ClaimsIdentity identity))
                        return new AuthenticationTicket(null, new AuthenticationProperties());

                    return new AuthenticationTicket(identity, new AuthenticationProperties());
                }
            }
            catch (Exception ex)
            {
                var msg = JsonConvert.SerializeObject(ex, new JsonSerializerSettings() { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });
                Trace.TraceError($"{msg}");
                return null;
            }
        }
        
        public static List<SecurityKey> GetPublicKeysCached(IDependencyScope dependencyScope)
        {
            const string memoryGroup = "AADAccess";
            const string key = "PublicKeys";
            
            var cacheProvider = (ICacheProvider<List<SecurityKey>>)dependencyScope.GetService(typeof(ICacheProvider<List<SecurityKey>>)) ??
                                new MemoryCacheProvider<List<SecurityKey>>(memoryGroup, 12 * 60, true);
            var lockProvider = (ILockProvider<List<SecurityKey>> )dependencyScope.GetService(typeof(ILockProvider<List<SecurityKey>>)) ??
                               new LockProvider<List<SecurityKey>>(memoryGroup);

            return lockProvider.PerformActionLocked(key, () => cacheProvider.Get(key, GetPublicKeys));
        }

        public static List<SecurityKey> GetPublicKeys()
        {
            using (var client = new WebClient())
            {

                var openIdConfigStr = client.DownloadString("https://login.microsoftonline.com/common/.well-known/openid-configuration");
                var openIdConfig = JsonConvert.DeserializeObject<AADOpenIdConfig>(openIdConfigStr);
                var jwkeys = client.DownloadString(openIdConfig.jwks_uri);
                var response = JsonConvert.DeserializeObject<AADPublicKeys>(jwkeys);

                var keys = new List<SecurityKey>();
                foreach (var webKey in response.keys)
                {
                    var e = Decode(webKey.e);
                    var n = Decode(webKey.n);

                    var key = new RsaSecurityKey(new RSAParameters { Exponent = e, Modulus = n })
                    {
                        KeyId = webKey.kid
                    };

                    keys.Add(key);
                }

                return keys;
            }

        }

        private async Task<string> GetAccessToken(IDependencyScope dependencyScope, IOwinContext owin)
        {
            const string memoryGroup = "SharePoint.Authentication.SharePointSession";
            var cacheProvider = (ICacheProvider<string>)dependencyScope.GetService(typeof(ICacheProvider<string>)) ??
                                new MemoryCacheProvider<string>(memoryGroup, _sharePointAuthenticationOptions.TokenCacheDurationInMinutes, true);
            var lockProvider = (ILockProvider<string> )dependencyScope.GetService(typeof(ILockProvider<string>)) ??
                               new LockProvider<string>(memoryGroup);
            var lowTrustTokenHelper = (LowTrustTokenHelper)dependencyScope.GetService(typeof(LowTrustTokenHelper));
            var contextTokenProvider = (ISharePointSessionProvider)dependencyScope.GetService(typeof(ISharePointSessionProvider));
            
            async Task<string> GetNewAccessToken(Guid sessionId)
            {
                try
                {
                    var sharePointSession = await contextTokenProvider.GetSharePointSession(sessionId);
                    var contextTokenObj = lowTrustTokenHelper.ReadAndValidateContextToken(sharePointSession.ContextToken, sharePointSession.ContextTokenAuthority);

                    if (contextTokenObj.ValidTo < DateTimeOffset.Now)
                        return null;

                    var accessToken = lowTrustTokenHelper.GetAccessToken(contextTokenObj, new Uri(sharePointSession.SharePointHostWebUrl).Authority);
                    return accessToken.AccessToken;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            var cookieValues = GetCookieValues(owin, "session-id");
            foreach (var cookieValue in cookieValues)
            {
                if (!Guid.TryParse(cookieValue, out var sessionId))
                {
                    continue;
                }

                var cacheKey = sessionId.ToString("N");
                var accessToken = await lockProvider.PerformActionLockedAsync(cacheKey, 
                    () => cacheProvider.GetAsync(cacheKey, () => GetNewAccessToken(sessionId)));

                if (string.IsNullOrWhiteSpace(accessToken)) continue;

                return accessToken;
            }

            return null;
        }

        private static IEnumerable<string> GetCookieValues(IOwinContext owin, string cookieName)
        {
            var cookieHeaders = owin.Request.Headers.Where(c => c.Key == "Cookie").ToList();
            foreach (var cookieHeader in cookieHeaders)
            {
                var cookieHeaderValues = cookieHeader.Value;
                var val = "";
                foreach (var cookieHeaderValue in cookieHeaderValues)
                {
                    var cookieParts = cookieHeaderValue.Split(';');
                    foreach (var cookie in cookieParts)
                    {
                        if (cookie.Trim().StartsWith($"{cookieName}="))
                        {
                            val = cookie.Trim().Replace($"{cookieName}=", "");
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(val))
                    continue;

                yield return val;
            }
        }

        public static byte[] Decode(string arg)
        {
            if (arg == null)
            {
                throw new ArgumentNullException("arg");
            }

            var s = arg
                .PadRight(arg.Length + (4 - arg.Length % 4) % 4, '=')
                .Replace("_", "/")
                .Replace("-", "+");


            return Convert.FromBase64String(s);
        }
    }
}
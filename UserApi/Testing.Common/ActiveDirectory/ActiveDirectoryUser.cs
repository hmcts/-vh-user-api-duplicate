﻿using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Polly;

namespace Testing.Common.ActiveDirectory
{
    public static class ActiveDirectoryUser
    {
        private static string ApiBaseUrl => $"https://graph.microsoft.com/v1.0/{TestConfig.Instance.AzureAd.TenantId}";

        public static async Task<bool> IsUserInAGroup(string user, string groupName, string token)
        {
            var url = $@"{ApiBaseUrl}/users/{user}/memberOf";
            var groupsResult = await SendGraphApiRequest(url, HttpMethod.Get, token);
            return groupsResult.Contains(groupName);
        }

        public static async Task RemoveTheUserFromTheGroup(string user, string groupId, string token)
        {
            var url = $@"{ApiBaseUrl}/groups/{groupId}/members/{user}/$ref";
            await SendGraphApiRequest(url, HttpMethod.Delete, token);
            Console.WriteLine($"Deleted group '{groupId}' from user: {user}");
        }

        public static async Task DeleteTheUserFromAd(string user, string token)
        {
            var url = $@"{ApiBaseUrl}/users/{user}";
            await SendGraphApiRequest(url, HttpMethod.Delete, token);
            Console.WriteLine($"Deleted user: {user}");
        }

        private static async Task<string> SendGraphApiRequest(string url, HttpMethod method, string token)
        {
            var policy = Policy.HandleResult<HttpResponseMessage>(r => r.StatusCode == HttpStatusCode.NotFound)
                .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    (msg, time) => { Console.WriteLine($"Received {msg.Result.StatusCode} for {method} {url}"); });
           
            // sometimes the api can be slow to actually allow us to access the created instance, so retry if it fails the first time
            var result = await policy.ExecuteAsync(async () =>
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    var httpRequestMessage = new HttpRequestMessage(method, url);
                    return await client.SendAsync(httpRequestMessage);
                }
            });

            var response = await result.Content.ReadAsStringAsync();
            if (!result.IsSuccessStatusCode)
                throw new Exception($"Failed to execute {method} on {url}, got response {result.StatusCode}: {response}");

            return response;
        }
    }
}

using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using UserApi.Common;
using UserApi.Contract.Requests;
using UserApi.Helper;
using UserApi.Security;
using UserApi.Services.Models;

namespace UserApi.Services
{
    public interface IUserAccountService
    {
        Task<NewAdUserAccount> CreateUser(User newUser);
        Task<NewAdUserAccount> CreateUser(string firstName, string lastName, string displayName = null,
            string password = null);
        Task AddUserToGroup(User user, Group @group);
        Task UpdateAuthenticationInformation(string userId, string recoveryMail);

        /// <summary>
        /// Get a user in AD either via Object ID or UserPrincipalName
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        Task<User> GetUserById(string userId);
        Task<IList<User>> QueryUsers(string filter);
        Task<Group> GetGroupByName(string groupName);
        Task<Group> GetGroupById(string groupId);
        Task<List<Group>> GetGroupsForUser(string userId);
        Task ResetPassword(string userId, string password = null);
        Task<User> GetUserByFilter(string filter);
    }

    public class UserAccountService : IUserAccountService
    {
        private const string OdataType = "@odata.type";
        private const string GraphGroupType = "#microsoft.graph.group";
        private readonly TimeSpan _retryTimeout;
        private readonly ITokenProvider _tokenProvider;
        private readonly AzureAdConfiguration _azureAdConfiguration;

        public UserAccountService(ITokenProvider tokenProvider, IOptions<AzureAdConfiguration> azureAdConfigOptions)
        {
            _retryTimeout = TimeSpan.FromSeconds(60);
            _tokenProvider = tokenProvider;
            _azureAdConfiguration = azureAdConfigOptions.Value;
        }

        public async Task<NewAdUserAccount> CreateUser(User newUser)
        {
            var accessToken = _tokenProvider.GetClientAccessToken(_azureAdConfiguration.ClientId,
                _azureAdConfiguration.ClientSecret,
                _azureAdConfiguration.GraphApiBaseUri);

            HttpResponseMessage responseMessage;
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var stringContent = new StringContent(JsonConvert.SerializeObject(newUser));
                stringContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
                var httpRequestMessage =
                    new HttpRequestMessage(HttpMethod.Post, $"{_azureAdConfiguration.GraphApiBaseUri}v1.0/users");
                httpRequestMessage.Content = stringContent;
                responseMessage = await client.SendAsync(httpRequestMessage);
            }

            if (responseMessage.IsSuccessStatusCode)
            {
                var user = await responseMessage.Content.ReadAsAsync<User>();
                var adUserAccount = new NewAdUserAccount
                {
                    Username = user.UserPrincipalName,
                    OneTimePassword = newUser.PasswordProfile.Password,
                    UserId = user.Id
                };
                return adUserAccount;
            }

            var message = $"Failed to add create user {newUser.UserPrincipalName}";
            var reason = await responseMessage.Content.ReadAsStringAsync();
            throw new UserServiceException(message, reason);
        }

        public async Task<NewAdUserAccount> CreateUser(string firstName, string lastName, string displayName = null,
            string password = null)
        {
            const string createdPassword = "Password123";
            var userDisplayName = displayName ?? $@"{firstName} {lastName}";
            var userPrincipalName = $@"{firstName}.{lastName}@hearings.reform.hmcts.net".ToLower();

            var user = new User
            {
                AccountEnabled = true,
                DisplayName = userDisplayName,
                MailNickname = $@"{firstName}.{lastName}",
                PasswordProfile = new PasswordProfile
                {
                    ForceChangePasswordNextSignIn = true,
                    Password = createdPassword
                },
                GivenName = firstName,
                Surname = lastName,
                UserPrincipalName = userPrincipalName
            };

            return await CreateUser(user);
        }

        public async Task AddUserToGroup(User user, Group @group)
        {
            var accessToken = _tokenProvider.GetClientAccessToken(_azureAdConfiguration.ClientId, _azureAdConfiguration.ClientSecret,
                _azureAdConfiguration.GraphApiBaseUri);

            var body = new CustomDirectoryObject
            {
                ObjectDataId = $"{_azureAdConfiguration.GraphApiBaseUri}v1.0/directoryObjects/{user.Id}"
            };

            HttpResponseMessage responseMessage;
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var stringContent = new StringContent(JsonConvert.SerializeObject(body));
                stringContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
                var httpRequestMessage =
                    new HttpRequestMessage(HttpMethod.Post,
                        $@"{_azureAdConfiguration.GraphApiBaseUri}beta/groups/{group.Id}/members/$ref")
                    {
                        Content = stringContent
                    };
                responseMessage = await client.SendAsync(httpRequestMessage);
            }

            if (responseMessage.IsSuccessStatusCode) return;

            var message = $"Failed to add user {user.Id} to group {group.Id}";
            var reason = await responseMessage.Content.ReadAsStringAsync();
            throw new UserServiceException(message, reason);
        }

        public async Task UpdateAuthenticationInformation(string userId, string recoveryMail)
        {
            var timeout = DateTime.Now.Add(_retryTimeout);
            await UpdateAuthenticationInformation(userId, recoveryMail, timeout);
        }

        private async Task UpdateAuthenticationInformation(string userId, string recoveryMail, DateTime timeout)
        {
            var accessToken = _tokenProvider.GetClientAccessToken(_azureAdConfiguration.ClientId,
                _azureAdConfiguration.ClientSecret, "https://graph.windows.net/");

            var model = new UpdateAuthenticationInformationRequest
            {
                OtherMails = new List<string> { recoveryMail }
            };

            HttpResponseMessage responseMessage;
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var stringContent = new StringContent(JsonConvert.SerializeObject(model));
                stringContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
                var httpRequestMessage =
                    new HttpRequestMessage(HttpMethod.Patch,
                        $"https://graph.windows.net/{_azureAdConfiguration.TenantId}/users/{userId}?api-version=1.6")
                    {
                        Content = stringContent
                    };
                responseMessage = await client.SendAsync(httpRequestMessage);
            }

            if (responseMessage.IsSuccessStatusCode) return;

            var reason = await responseMessage.Content.ReadAsStringAsync();

            // If it's 404 try it again as the user might simply not have become "ready" in AD
            if (responseMessage.StatusCode == HttpStatusCode.NotFound)
            {
                if (DateTime.Now > timeout)
                {
                    throw new UserServiceException("Timed out trying to update alternative address for ${userId}", reason);
                }
                ApplicationLogger.Trace("APIFailure", "GraphAPI 404 PATCH /users/{id}", $"Failed to update authentication information for user {userId}, will retry.");
                await UpdateAuthenticationInformation(userId, recoveryMail, timeout);
                return;
            }

            var message = $"Failed to update alternative email address for {userId}";
            throw new UserServiceException(message, reason);
        }

        public async Task<User> GetUserById(string userId)
        {
            var accessToken = _tokenProvider.GetClientAccessToken(_azureAdConfiguration.ClientId,
                _azureAdConfiguration.ClientSecret,
                _azureAdConfiguration.GraphApiBaseUri);

            HttpResponseMessage responseMessage;
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var httpRequestMessage =
                    new HttpRequestMessage(HttpMethod.Get, $"{_azureAdConfiguration.GraphApiBaseUri}v1.0/users/{userId}");
                responseMessage = await client.SendAsync(httpRequestMessage);
            }

            if (responseMessage.IsSuccessStatusCode)
            {
                return await responseMessage.Content.ReadAsAsync<User>();
            }

            if (responseMessage.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            var message = $"Failed to get user by id {userId}";
            var reason = await responseMessage.Content.ReadAsStringAsync();
            throw new UserServiceException(message, reason);
        }

        public async Task<IList<User>> QueryUsers(string filter)
        {
            var accessToken = _tokenProvider.GetClientAccessToken(_azureAdConfiguration.ClientId,
                _azureAdConfiguration.ClientSecret,
                _azureAdConfiguration.GraphApiBaseUri);

            HttpResponseMessage responseMessage;
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var httpRequestMessage =
                    new HttpRequestMessage(HttpMethod.Get, $"{_azureAdConfiguration.GraphApiBaseUri}v1.0/users?$filter={filter}");
                responseMessage = await client.SendAsync(httpRequestMessage);

            }

            if (responseMessage.IsSuccessStatusCode)
            {
                return (await responseMessage.Content.ReadAsAsync<AzureAdGraphQueryResponse<User>>()).Value;
            }

            var message = $"Failed to get query users with filter {filter}";
            var reason = await responseMessage.Content.ReadAsStringAsync();
            throw new UserServiceException(message, reason);
        }

        public async Task<User> GetUserByFilter(string filter)
        {
            var accessToken = _tokenProvider.GetClientAccessToken(_azureAdConfiguration.ClientId,
                _azureAdConfiguration.ClientSecret, "https://graph.windows.net/");

            HttpResponseMessage responseMessage;
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var httpRequestMessage =
                    new HttpRequestMessage(HttpMethod.Get,
                        $"https://graph.windows.net/{_azureAdConfiguration.TenantId}/users?$filter={filter}&api-version=1.6");
                responseMessage = await client.SendAsync(httpRequestMessage);
            }

            if (responseMessage.IsSuccessStatusCode)
            {
                var queryResponse = await responseMessage.Content.ReadAsAsync<AzureAdGraphQueryResponse<AzureAdGraphUserResponse>>();
                if (!queryResponse.Value.Any())
                {
                    return null;
                }

                var adUser = queryResponse.Value.First();
                return new User
                {
                    Id = adUser.ObjectId,
                    DisplayName = adUser.DisplayName,
                    UserPrincipalName = adUser.UserPrincipalName,
                    GivenName = adUser.GivenName,
                    Surname = adUser.Surname,
                    Mail = adUser.OtherMails?.FirstOrDefault()
                };
            }

            if (responseMessage.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            var message = $"Failed to search user with filter {filter}";
            var reason = await responseMessage.Content.ReadAsStringAsync();
            throw new UserServiceException(message, reason);
        }

        public async Task<Group> GetGroupByName(string groupName)
        {
            var accessToken = _tokenProvider.GetClientAccessToken(_azureAdConfiguration.ClientId,
                _azureAdConfiguration.ClientSecret, _azureAdConfiguration.GraphApiBaseUri);

            HttpResponseMessage responseMessage;
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var httpRequestMessage = new HttpRequestMessage(HttpMethod.Get,
                    $"{_azureAdConfiguration.GraphApiBaseUri}v1.0/groups?$filter=displayName eq '{groupName}'");
                responseMessage = await client.SendAsync(httpRequestMessage);

            }

            if (responseMessage.IsSuccessStatusCode)
            {
                var queryResponse = await responseMessage.Content.ReadAsAsync<GraphQueryResponse>();
                return queryResponse.Value?.FirstOrDefault();
            }

            var message = $"Failed to get group by name {groupName}";
            var reason = await responseMessage.Content.ReadAsStringAsync();
            throw new UserServiceException(message, reason);
        }

        public async Task<Group> GetGroupById(string groupId)
        {
            var accessToken = _tokenProvider.GetClientAccessToken(_azureAdConfiguration.ClientId,
                _azureAdConfiguration.ClientSecret, _azureAdConfiguration.GraphApiBaseUri);

            HttpResponseMessage responseMessage;
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var httpRequestMessage =
                    new HttpRequestMessage(HttpMethod.Get, $"{_azureAdConfiguration.GraphApiBaseUri}v1.0/groups/{groupId}");
                responseMessage = await client.SendAsync(httpRequestMessage);
            }

            if (responseMessage.IsSuccessStatusCode)
            {
                return await responseMessage.Content.ReadAsAsync<Group>();
            }

            if (responseMessage.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            var message = $"Failed to get group by id {groupId}";
            var reason = await responseMessage.Content.ReadAsStringAsync();
            throw new UserServiceException(message, reason);
        }

        public async Task<List<Group>> GetGroupsForUser(string userId)
        {
            var accessToken = _tokenProvider.GetClientAccessToken(_azureAdConfiguration.ClientId,
                _azureAdConfiguration.ClientSecret, _azureAdConfiguration.GraphApiBaseUri);

            HttpResponseMessage responseMessage;
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var httpRequestMessage = new HttpRequestMessage(HttpMethod.Get,
                    $"{_azureAdConfiguration.GraphApiBaseUri}v1.0/users/{userId}/memberOf");
                responseMessage = await client.SendAsync(httpRequestMessage);
            }

            if (responseMessage.IsSuccessStatusCode)
            {
                var queryResponse = await responseMessage.Content.ReadAsAsync<DirectoryObject>();
                var groupArray = JArray.Parse(queryResponse?.AdditionalData["value"].ToString());

                var groups = new List<Group>();
                foreach (var item in groupArray.Children())
                {
                    var itemProperties = item.Children<JProperty>();
                    var type = itemProperties.FirstOrDefault(x => x.Name == OdataType);

                    //If #microsoft.graph.directoryRole ignore the group mappings
                    if (type.Value.ToString() == GraphGroupType)
                    {
                        var group = JsonConvert.DeserializeObject<Group>(item.ToString());
                        groups.Add(group);
                    }
                }
                return groups;
            }

            if (responseMessage.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            var message = $"Failed to get group for user {userId}";
            var reason = await responseMessage.Content.ReadAsStringAsync();
            throw new UserServiceException(message, reason);
        }

        public async Task ResetPassword(string userId, string password = null)
        {
            var accessToken = _tokenProvider.GetClientAccessToken(_azureAdConfiguration.ClientId,
                _azureAdConfiguration.ClientSecret, _azureAdConfiguration.GraphApiBaseUri);

            var createdPassword = password ?? new PasswordGenerator().IncludeLowercase().IncludeUppercase()
                                      .IncludeNumeric().IncludeSpecial().LengthRequired(8).Next();

            var model = new User
            {
                PasswordProfile = new PasswordProfile
                {
                    Password = createdPassword,
                    ForceChangePasswordNextSignIn = true
                }
            };

            HttpResponseMessage responseMessage;
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var stringContent = new StringContent(JsonConvert.SerializeObject(model));
                stringContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
                var httpRequestMessage =
                    new HttpRequestMessage(HttpMethod.Patch, $"{_azureAdConfiguration.GraphApiBaseUri}v1.0/users/{userId}")
                    {
                        Content = stringContent
                    };
                responseMessage = await client.SendAsync(httpRequestMessage);
            }

            if (responseMessage.IsSuccessStatusCode) return;
            var message = $"Failed to get group for user {userId}";
            var reason = await responseMessage.Content.ReadAsStringAsync();
            throw new UserServiceException(message, reason);
        }
    }
}
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Graph;
using UserApi.Services.Models;

namespace UserApi.Services
{
    public interface IUserAccountService
    {
        Task<NewAdUserAccount> CreateUser(string firstName, string lastName, string displayName = null);

        Task AddUserToGroup(User user, Group group);
        Task UpdateAuthenticationInformation(string userId, string recoveryMail);

        /// <summary>
        ///     Get a user in AD either via Object ID or UserPrincipalName
        /// </summary>
        /// <param name="userId"></param>
        /// <returns>The User.</returns>
        Task<User> GetUserById(string userId);

        Task<Group> GetGroupByName(string groupName);
        Task<Group> GetGroupById(string groupId);
        Task<List<Group>> GetGroupsForUser(string userId);
        Task<User> GetUserByFilter(string filter);
        Task<List<UserResponse>> GetJudges();
    }
}
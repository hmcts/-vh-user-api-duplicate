﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Moq;
using NUnit.Framework;
using Testing.Common.Assertions;
using UserApi.Caching;
using UserApi.Contract.Requests;
using UserApi.Contract.Responses;
using UserApi.Controllers;
using UserApi.Responses;
using UserApi.Services;
using UserApi.Services.Models;
namespace UserApi.UnitTests.Controllers
{
    public class UserAccountsControllerTests
    {
        private UserController _controller;
        private Mock<IUserAccountService> _userAccountService;
        private CreateUserRequest _request;
        private NewAdUserAccount _newAdUserAccount;
        private Mock<ICache> _cache;
        private Settings _settings;
        protected const string Domain = "@hearings.test.server.net";
        private Mock<ILogger<UserController>> _logger;

        [SetUp]
        public void Setup()
        {
            _logger = new Mock<ILogger<UserController>>();
            _userAccountService = new Mock<IUserAccountService>();
            var representativeGroups = new List<Group> {new Group {DisplayName = "ProfUser"}};
            _userAccountService.Setup(x => x.GetGroupsForUserAsync(It.IsAny<string>()))
                .ReturnsAsync(representativeGroups);
            var config = TelemetryConfiguration.CreateDefault();
            var client = new TelemetryClient(config);
            _settings = new Settings { IsLive = true,
                                        ReformEmail = Domain.Replace("@", ""),
                                        AdGroup = new AdGroup
                                        {
                                            Administrator = "A",
                                            CaseType = "CT",
                                            External = "E",
                                            Judge = "J",
                                            ProfessionalUser = "ProfUser",
                                            JudgesTestGroup = "JTG"
                                        }
                                    };

            _request = Builder<CreateUserRequest>.CreateNew()
                .With(x => x.FirstName = "John")
                .With(x => x.LastName = "doe")
                .With(x => x.RecoveryEmail = "john.doe@hmcts.net")
                .Build();
            _newAdUserAccount = new NewAdUserAccount { UserId = "TestUserId", Username = "TestUserName", OneTimePassword = "TestPassword" };
            _userAccountService.Setup(u => u.CreateUserAsync(_request.FirstName, _request.LastName, _request.RecoveryEmail, _request.IsTestUser)).ReturnsAsync(_newAdUserAccount);
            _cache = new Mock<ICache>();

            
            _controller = new UserController(_userAccountService.Object, client, _cache.Object, _settings, _logger.Object);
        }

        [Test]
        public async Task Should_create_user_and_return_NewUserResponse_for_given_request()
        {
            var actionResult = (CreatedAtRouteResult)await _controller.CreateUser(_request);

            actionResult.Should().NotBeNull();
            actionResult.RouteName.Should().Be("GetUserByAdUserId");
            actionResult.StatusCode.Should().Be((int)HttpStatusCode.Created);
            var response = (NewUserResponse)actionResult.Value;
            response.UserId.Should().Be(_newAdUserAccount.UserId);
            response.Username.Should().Be(_newAdUserAccount.Username);
            response.OneTimePassword.Should().Be(_newAdUserAccount.OneTimePassword);
        }

        [Test]
        public async Task Should_return_BadRequest_for_given_invalid_create_user_request()
        {
            var actionResult = (BadRequestObjectResult)await _controller.CreateUser(new CreateUserRequest() { FirstName = "Test", LastName = "Tester" });

            actionResult.Should().NotBeNull();
            actionResult.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
            var serializableErrors = (SerializableError)actionResult.Value;
            serializableErrors.ContainsKeyAndErrorMessage("RecoveryEmail","recovery email cannot be empty");
        }

        [Test]
        public async Task Should_return_ConflictObjectResult_with_UserExistsException()
        {
            _userAccountService.Setup(u => u.CreateUserAsync(_request.FirstName, _request.LastName, _request.RecoveryEmail, _request.IsTestUser)).ThrowsAsync(new UserExistsException("User exists","TestUser"));

            var actionResult = (ConflictObjectResult)await _controller.CreateUser(_request);

            actionResult.Should().NotBeNull();
            actionResult.StatusCode.Should().Be((int)HttpStatusCode.Conflict);
            actionResult.Value.ToString().Should().Be("{ Message = User already exists, Code = UserExists, Username = TestUser }");
        }

        [Test]
        public async Task Should_get_user_by_user_id_from_api()
        {
            string userId = Guid.NewGuid().ToString();
            var userResponse = new User
            {
                DisplayName = "Sample User",
                GivenName = "User",
                Surname = "Sample"
            };
            var response = new UserProfile
            {
                DisplayName = "Sample User",
                FirstName = "User",
                LastName = "Sample"
            };

            var filter = $"objectId  eq '{userId}'";
            _userAccountService.Setup(x => x.GetUserByFilterAsync(filter)).Returns(Task.FromResult(userResponse));

            var actionResult = (OkObjectResult) await _controller.GetUserByAdUserId(userId);
            var actualResponse = (UserProfile) actionResult.Value;
            actualResponse.DisplayName.Should().BeSameAs(response.DisplayName);
            actualResponse.FirstName.Should().BeSameAs(response.FirstName);
            actualResponse.LastName.Should().BeSameAs(response.LastName);
        }
        
        [Test]
        public async Task Should_get_user_by_user_id_from_api_with_special_characters()
        {
            string userId = "john.o'conner@hearings.reform.hmcts.net ";
            var userResponse = new User
            {
                DisplayName = "Sample User",
                GivenName = "User",
                Surname = "Sample"
            };
            var response = new UserProfile
            {
                DisplayName = "Sample User",
                FirstName = "User",
                LastName = "Sample"
            };

            var filter = $"objectId  eq '{userId.Replace("'", "''")}'";
            _userAccountService.Setup(x => x.GetUserByFilterAsync(filter)).Returns(Task.FromResult(userResponse));

            var actionResult = (OkObjectResult) await _controller.GetUserByAdUserId(userId);
            var actualResponse = (UserProfile) actionResult.Value;
            actualResponse.DisplayName.Should().BeSameAs(response.DisplayName);
            actualResponse.FirstName.Should().BeSameAs(response.FirstName);
            actualResponse.LastName.Should().BeSameAs(response.LastName);
        }

        [Test]
        public async Task Should_return_badrequest_with_invalid_userId()
        {
            var userId = string.Empty;

            var actionResult = (BadRequestObjectResult)await _controller.GetUserByAdUserId(userId);

            actionResult.Should().NotBeNull();
            actionResult.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
            ((SerializableError)actionResult.Value).ContainsKeyAndErrorMessage(nameof(userId), "username cannot be empty");
        }

        [Test]
        public async Task Should_return_notfound_with_no_matching_user_profile()
        {
            var userId = Guid.NewGuid().ToString();
            _userAccountService.Setup(x => x.GetUserByFilterAsync(It.IsAny<string>())).Returns(Task.FromResult((User)null));

            var actionResult = (NotFoundObjectResult)await _controller.GetUserByAdUserId(userId);

            actionResult.Should().NotBeNull();
            actionResult.StatusCode.Should().Be((int)HttpStatusCode.NotFound);
            ((ModelStateDictionary)actionResult.Value).ContainsKeyAndErrorMessage(nameof(userId), "user does not exist");
        }


        [Test]
        public async Task Should_get_user_by_user_name_from_api()
        {
            const string userName = "sample.user'test@hearings.test.server.net";
            var userResponse = new User
            {
                DisplayName = "Sample User",
                GivenName = "User",
                Surname = "Sample"
            };
            var response = new UserProfile
            {
                DisplayName = "Sample User",
                FirstName = "User",
                LastName = "Sample"
            };

            var filter = $"userPrincipalName  eq '{userName.Replace("'", "''")}'";
            _userAccountService.Setup(x => x.GetUserByFilterAsync(filter)).Returns(Task.FromResult(userResponse));

            var actionResult = (OkObjectResult) await _controller.GetUserByUserName(userName);
            var actualResponse = (UserProfile) actionResult.Value;
            actualResponse.DisplayName.Should().BeSameAs(response.DisplayName);
            actualResponse.FirstName.Should().BeSameAs(response.FirstName);
            actualResponse.LastName.Should().BeSameAs(response.LastName);
            _userAccountService.Verify(x => x.GetUserByFilterAsync(filter),Times.Once);
        }

        [Test]
        public async Task Should_get_unauthorized_when_get_by_user_name_from_api()
        {
            const string userName = "sample.user@hearings.test.server.net";
            _userAccountService
                .Setup(x => x.GetUserByFilterAsync(It.IsAny<string>()))
                .Throws(new UnauthorizedAccessException("unauthorized"));

            var result = (await _controller.GetUserByUserName(userName)) as UnauthorizedObjectResult;
            Assert.NotNull(result);
        }

        [Test]
        public async Task Should_get_notfound_with_no_matching_user_profile()
        {
            const string userName = "sample.user@hearings.test.server.net";
            _userAccountService.Setup(x => x.GetUserByFilterAsync(It.IsAny<string>())).Returns(Task.FromResult((User)null));

            var actionResult = (NotFoundObjectResult)await _controller.GetUserByUserName(userName);

            actionResult.Should().NotBeNull();
            actionResult.StatusCode.Should().Be((int)HttpStatusCode.NotFound);
            ((ModelStateDictionary)actionResult.Value).ContainsKeyAndErrorMessage(nameof(userName), "user principal name does not exist");
        }

        [Test]
        public async Task Should_return_badrequest_with_invalid_userName()
        {
            var userName = string.Empty;

            var actionResult = (BadRequestObjectResult)await _controller.GetUserByUserName(userName);
            actionResult.Should().NotBeNull();
            actionResult.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
            ((SerializableError)actionResult.Value).ContainsKeyAndErrorMessage(nameof(userName), "user principal name cannot be empty");
        }

        [Test]
        public async Task Should_get_user_by_email_from_api()
        {
            const string email = "sample.user'test@gmail.com";
            var userResponse = new User
            {
                DisplayName = "Sample User",
                GivenName = "User",
                Surname = "Sample"
            };
            var response = new UserProfile
            {
                DisplayName = "Sample User",
                FirstName = "User",
                LastName = "Sample"
            };

            var filter = $"otherMails/any(c:c eq '{email.Replace("'", "''")}')";
            _userAccountService.Setup(x => x.GetUserByFilterAsync(filter)).Returns(Task.FromResult(userResponse));

            var actionResult = (OkObjectResult) await _controller.GetUserByEmail(email);
            var actualResponse = (UserProfile) actionResult.Value;
            actualResponse.DisplayName.Should().BeSameAs(response.DisplayName);
            actualResponse.FirstName.Should().BeSameAs(response.FirstName);
            actualResponse.LastName.Should().BeSameAs(response.LastName);
            _userAccountService.Verify(x => x.GetUserByFilterAsync(filter), Times.Once);
        }

        [Test]
        public async Task Should_return_badrequest_with_no_emailid()
        {
            var email = string.Empty;

            var actionResult = (BadRequestObjectResult)await _controller.GetUserByEmail(email);
            actionResult.Should().NotBeNull();
            actionResult.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
            ((SerializableError)actionResult.Value).ContainsKeyAndErrorMessage(nameof(email), "email cannot be empty");
        }

        [Test]
        public async Task Should_return_badrequest_with_invalid_email()
        {
            var email = "invalid@email@com";

            var actionResult = (NotFoundObjectResult)await _controller.GetUserByEmail(email);
            actionResult.Should().NotBeNull();
            actionResult.StatusCode.Should().Be((int)HttpStatusCode.NotFound);
            ((ModelStateDictionary)actionResult.Value).ContainsKeyAndErrorMessage("email", "email does not exist");
        }

        [Test]
        public async Task Should_get_users_for_group_by_group_id_from_api()
        {
            var response = new List<UserResponse>();
            var user = new UserResponse
            {
                DisplayName = "firstname lastname", FirstName = "firstname", LastName = "lastname", 
                Email = "firstname.lastname@hearings.test.server.net"
            };
            
            response.Add(user);
            
            user = new UserResponse
            {
                DisplayName = "firstname1 lastname1", FirstName = "firstname1", LastName = "lastname1", 
                Email = "firstname1.lastname1@hearings.test.server.net"
            };
            response.Add(user);

            var userList = new List<UserResponse>()
            {
                new UserResponse { DisplayName = "firstname lastname", FirstName = "firstname", LastName = "lastname", Email = "firstname.lastname@hearings.test.server.net" }
            };

            _cache.Setup(x => x.GetOrAddAsync(It.IsAny<Func<Task<IEnumerable<UserResponse>>>>()))
                .Callback(async (Func<Task<IEnumerable<UserResponse>>> factory) => await factory())
                .ReturnsAsync(response.AsEnumerable());

            var actionResult = (OkObjectResult)await _controller.GetJudges();
            var actualResponse = (List<UserResponse>)actionResult.Value;
            actualResponse.Count.Should().Be(2);
            actualResponse.FirstOrDefault().DisplayName.Should().BeSameAs(userList.FirstOrDefault().DisplayName);
        }

        [Test]
        public async Task Should_get_empty_user_response_without_judges()
        {
            _cache.Setup(x => x.GetOrAddAsync(It.IsAny<Func<Task<IEnumerable<UserResponse>>>>()))
                .Callback(async (Func<Task<IEnumerable<UserResponse>>> factory) => await factory())
                .ReturnsAsync((IEnumerable<UserResponse>) null);
            
            var actionResult = (OkObjectResult)await _controller.GetJudges();
            var actualResponse = (List<UserResponse>)actionResult.Value;
            actualResponse.Count.Should().Be(0);
        }

        [Test]
        public async Task Should_return_bad_request_for_update_user()
        {
            var username = " ";            

            (await _controller.UpdateUser(null)).Should().NotBeNull().And.BeAssignableTo<BadRequestObjectResult>();
            (await _controller.UpdateUser(string.Empty)).Should().NotBeNull().And.BeAssignableTo<BadRequestObjectResult>();
            var actionResult = (BadRequestObjectResult)await _controller.UpdateUser(username);

            actionResult.Should().NotBeNull();
            actionResult.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
            ((SerializableError)actionResult.Value).ContainsKeyAndErrorMessage(nameof(username), "username cannot be empty");
        }
        [Test]
        public async Task Should_update_the_password_for_a_user_that_exists_in_ad()
        {
            const string email = "known.user'test@gmail.com";
            var filter = $"userPrincipalName  eq '{email.Replace("'", "''")}'";
            var userResponse = new User
            {
                DisplayName = "Sample User",
                GivenName = "User",
                Surname = "Sample",
                UserPrincipalName = email
            };

            const string password = "Password123";
            _userAccountService.Setup(x => x.GetUserByFilterAsync(filter)).ReturnsAsync(userResponse);
            _userAccountService.Setup(x => x.UpdateUserPasswordAsync(userResponse.UserPrincipalName)).ReturnsAsync(password);

            var result = await _controller.UpdateUser(email);
            
            result.Should().NotBeNull();
            result.Should().BeAssignableTo<OkObjectResult>();
            var response = (OkObjectResult) result;
            response.Should().NotBeNull();
            response.Value.Should().NotBeNull().And.BeAssignableTo<UpdateUserResponse>();
            response.Value.As<UpdateUserResponse>().NewPassword.Should().Be(password);
            
            _userAccountService.Verify(x => x.GetUserByFilterAsync(filter), Times.Once);
        }
    }
}

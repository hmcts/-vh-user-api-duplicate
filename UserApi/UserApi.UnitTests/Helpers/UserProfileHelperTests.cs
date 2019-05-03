using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using UserApi.Helper;
using UserApi.Services;
using System.Threading.Tasks;
using Microsoft.Graph;

namespace UserApi.UnitTests.Helpers
{
    public class UserProfileHelperTests
    {
        private Mock<IUserAccountService> _accountService;
        private UserProfileHelper _helper;

        private const string Filter = "some filter";

        [SetUp]
        public void Setup()
        {
            _accountService = new Mock<IUserAccountService>();
            _helper = new UserProfileHelper(_accountService.Object);
        }
        
        [Test]
        public async Task should_return_case_admin_for_user_with_money_claims_group()
        {
            GivenFilterReturnsUserWithGroups(AdGroup.MoneyClaims);
            
            var userProfile = await _helper.GetUserProfile(Filter);

            userProfile.UserRole.Should().Be("CaseAdmin");
        }
        
        [Test]
        public async Task should_return_case_admin_for_user_with_financial_remedy_group()
        {
            GivenFilterReturnsUserWithGroups(AdGroup.FinancialRemedy);
            
            var userProfile = await _helper.GetUserProfile(Filter);

            userProfile.UserRole.Should().Be("CaseAdmin");
        }
        
        [Test]
        public async Task should_return_judge_for_user_with_internal_and_virtualroomjudge()
        {
            GivenFilterReturnsUserWithGroups(AdGroup.VirtualRoomJudge);
            
            var userProfile = await _helper.GetUserProfile(Filter);

            userProfile.UserRole.Should().Be("Judge");
        }
        
        [Test]
        public async Task should_return_vhadmin_for_user_with_internal_and_virtualroomadministrator()
        {
            GivenFilterReturnsUserWithGroups(AdGroup.VirtualRoomAdministrator);
            
            var userProfile = await _helper.GetUserProfile(Filter);

            userProfile.UserRole.Should().Be("VhOfficer");
        }
        
        [Test]
        public async Task should_return_vhadmin_for_user_with_both_vho_groups_and_case_admin_group()
        {
            GivenFilterReturnsUserWithGroups(AdGroup.VirtualRoomAdministrator, AdGroup.FinancialRemedy);
            
            var userProfile = await _helper.GetUserProfile(Filter);

            userProfile.UserRole.Should().Be("VhOfficer");
        }
        
        [Test]
        public async Task should_return_representative_for_user_with_external_and_virtualcourtroomprofessional_groups()
        {
            GivenFilterReturnsUserWithGroups(AdGroup.VirtualRoomProfessionalUser);
            
            var userProfile = await _helper.GetUserProfile(Filter);

            userProfile.UserRole.Should().Be("Representative");
        }

        [Test]
        public async Task should_return_individual_for_user_with_external_group()
        {
            GivenFilterReturnsUserWithGroups(AdGroup.External);
            
            var userProfile = await _helper.GetUserProfile(Filter);

            userProfile.UserRole.Should().Be("Individual");
        }
        
        [Test]
        public void should_raise_exception_if_user_lacks_video_hearing_groups()
        {
            GivenFilterReturnsUserWithGroups();

            Assert.ThrowsAsync<UnauthorizedAccessException>(() => _helper.GetUserProfile(Filter),
                "Matching user is not registered with valid groups");
        }
        
        [Test]
        public async Task should_return_null_for_no_user_found()
        {
            _accountService.Setup(x => x.GetUserByFilter(Filter)).ReturnsAsync((User) null);
            
            var userProfile = await _helper.GetUserProfile(Filter);

            userProfile.Should().BeNull();
        }
        
        [Test]
        public async Task should_return_case_types_for_case_admin()
        {
            var caseTypes = new[] { AdGroup.MoneyClaims, AdGroup.FinancialRemedy };
            GivenFilterReturnsUserWithGroups(caseTypes);
            
            var userProfile = await _helper.GetUserProfile(Filter);

            userProfile.CaseType.Count.Should().Be(2);
            userProfile.CaseType.Should().Contain(AdGroup.MoneyClaims.ToString());
            userProfile.CaseType.Should().Contain(AdGroup.FinancialRemedy.ToString());
        }
        
        [Test]
        public async Task should_return_user_data()
        {
            var user = new User
            {
                Id = Guid.NewGuid().ToString(),
                Mail = "bob@contoso.com",
                DisplayName = "Bob McGregor",
                GivenName = "Bob",
                Surname = "McGregor",
                UserPrincipalName = "bob.mcgregor@hearings.reform.hmcts.net"
            };
            GivenFilterReturnsUserWithGroups(user, AdGroup.External);
            
            var userProfile = await _helper.GetUserProfile(Filter);

            userProfile.DisplayName.Should().Be(user.DisplayName);
            userProfile.FirstName.Should().Be(user.GivenName);
            userProfile.LastName.Should().Be(user.Surname);
            userProfile.Email.Should().Be(user.Mail);
            userProfile.UserId.Should().Be(user.Id);
            userProfile.UserName.Should().Be(user.UserPrincipalName);
        }

        private void GivenFilterReturnsUserWithGroups(User user, params AdGroup[] groupDisplayNames)
        {
            _accountService.Setup(x => x.GetUserByFilter(Filter))
                .ReturnsAsync(user);

            var groups = groupDisplayNames.Select(aadGroup => new Group { DisplayName = aadGroup.ToString() }).ToArray();
            _accountService.Setup(x => x.GetGroupsForUser(user.Id))
                .ReturnsAsync(new List<Group>(groups));
        }

        private void GivenFilterReturnsUserWithGroups(params AdGroup[] groupDisplayNames)
        {
            var user = new User {Id = Guid.NewGuid().ToString()};
            GivenFilterReturnsUserWithGroups(user, groupDisplayNames);
        }        
    }
}
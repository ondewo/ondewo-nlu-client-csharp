using System;
using System.Linq;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Ondewo.Nlu;
using Xunit;

namespace Ondewo.Nlu.Client.Tests
{
    /// <summary>
    /// The product-specific half of the suite: concrete assertions against the ONDEWO NLU API,
    /// spelled out with real message, field, enum and RPC names.
    /// <para>
    /// This is the only test file that has to be rewritten when the setup is replicated to another
    /// ONDEWO product - <see cref="GeneratedStubsTests"/> carries over unchanged.
    /// </para>
    /// </summary>
    public class NluStubsTests
    {
        private const string DummyTarget = "http://localhost:50051";

        [Fact]
        public void UserRoundTripsEveryScalarFieldKind()
        {
            var user = new User
            {
                UserId = "user-42",
                DisplayName = "Ada Lovelace",
                UserEmail = "ada@ondewo.com",
                ServerRoleId = 7,
                UserProfilePicture = ByteString.CopyFromUtf8("PNG"),
                CreatedBy = "creator-1",
            };

            byte[] bytes = user.ToByteArray();
            User parsed = User.Parser.ParseFrom(bytes);

            Assert.NotEmpty(bytes);
            Assert.Equal(user, parsed);
            Assert.Equal("user-42", parsed.UserId);
            Assert.Equal("Ada Lovelace", parsed.DisplayName);
            Assert.Equal("ada@ondewo.com", parsed.UserEmail);
            Assert.Equal(7u, parsed.ServerRoleId);
            Assert.Equal(ByteString.CopyFromUtf8("PNG"), parsed.UserProfilePicture);
            Assert.Equal("creator-1", parsed.CreatedBy);
        }

        [Fact]
        public void UserInfoRoundTripsANestedMessageAndAMapField()
        {
            var userInfo = new UserInfo
            {
                User = new User { UserId = "user-42", DisplayName = "Ada Lovelace" },
            };
            userInfo.ProjectRoles.Add(
                "project-1",
                new ProjectRole { RoleId = 3, Name = "PROJECT_DEVELOPER" });

            UserInfo parsed = UserInfo.Parser.ParseFrom(userInfo.ToByteArray());

            Assert.Equal(userInfo, parsed);
            Assert.Equal("user-42", parsed.User.UserId);
            Assert.Single(parsed.ProjectRoles);
            Assert.Equal(3u, parsed.ProjectRoles["project-1"].RoleId);
            Assert.Equal("PROJECT_DEVELOPER", parsed.ProjectRoles["project-1"].Name);
        }

        [Fact]
        public void RepeatedFieldRoundTripsThroughAListResponse()
        {
            var response = new ListUsersResponse();
            response.Users.Add(new User { UserId = "user-1" });
            response.Users.Add(new User { UserId = "user-2" });

            ListUsersResponse parsed = ListUsersResponse.Parser.ParseFrom(response.ToByteArray());

            Assert.Equal(response, parsed);
            Assert.Equal(new[] { "user-1", "user-2" }, parsed.Users.Select(user => user.UserId));
        }

        [Fact]
        public void UnsetScalarFieldsCarryTheProto3DefaultsAndStayOffTheWire()
        {
            var user = new User();

            Assert.Equal(string.Empty, user.UserId);
            Assert.Equal(0u, user.ServerRoleId);
            Assert.Equal(ByteString.Empty, user.UserProfilePicture);
            Assert.Null(user.CreatedAt);
            Assert.Empty(user.ToByteArray());
        }

        [Fact]
        public void EnumsStartAtTheirUnspecifiedZeroValue()
        {
            Assert.Equal(0, (int)AgentView.Unspecified);
            Assert.Equal(AgentView.Unspecified, default(AgentView));
            Assert.Equal(0, (int)DefaultProjectRole.ProjectUnspecified);
            Assert.Equal(DefaultProjectRole.ProjectUnspecified, default(DefaultProjectRole));

            // The C# name is PascalCased; the wire/JSON name is the one the server speaks.
            Assert.Equal(
                "AGENT_VIEW_UNSPECIFIED",
                AgentView.Unspecified.GetType()
                    .GetField(nameof(AgentView.Unspecified))
                    .GetCustomAttributes(typeof(Google.Protobuf.Reflection.OriginalNameAttribute), false)
                    .Cast<Google.Protobuf.Reflection.OriginalNameAttribute>()
                    .Single()
                    .Name);
        }

        [Fact]
        public void EnumFieldRoundTripsANonDefaultValue()
        {
            var request = new GetAgentRequest { AgentView = AgentView.Minimum };

            GetAgentRequest parsed = GetAgentRequest.Parser.ParseFrom(request.ToByteArray());

            Assert.Equal(AgentView.Minimum, parsed.AgentView);
            Assert.NotEmpty(request.ToByteArray());
        }

        [Fact]
        public void UsersClientBindsToAChannelAndExposesTheDeclaredRpcs()
        {
            using GrpcChannel channel = GrpcChannel.ForAddress(DummyTarget);

            var client = new Users.UsersClient(channel);

            Assert.NotNull(client);
            Assert.Equal("ondewo.nlu.Users", Users.Descriptor.FullName);
            Assert.Contains(Users.Descriptor.Methods, method => method.Name == "GetUser");

            string[] clientMethods = typeof(Users.UsersClient)
                .GetMethods()
                .Select(method => method.Name)
                .Distinct()
                .ToArray();

            foreach (string rpc in new[]
                     {
                         "CreateUser", "GetUser", "GetUserInfo", "UpdateUser", "DeleteUser",
                         "ListUsers", "ListUserInfos", "CreateServerRole", "ListServerRoles",
                     })
            {
                Assert.Contains(rpc, clientMethods);
                Assert.Contains(rpc + "Async", clientMethods);
            }
        }

        [Fact]
        public void AgentsClientIsGeneratedForTheSecondServiceToo()
        {
            using GrpcChannel channel = GrpcChannel.ForAddress(DummyTarget);

            var client = new Agents.AgentsClient(channel);

            Assert.NotNull(client);
            Assert.Equal("ondewo.nlu.Agents", Agents.Descriptor.FullName);
            Assert.Contains(Agents.Descriptor.Methods, method => method.Name == "CreateAgent");
        }
    }
}

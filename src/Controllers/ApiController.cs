﻿using Aiursoft.Pylon;
using Aiursoft.Pylon.Attributes;
using Aiursoft.Pylon.Models;
using Aiursoft.Pylon.Models.ForApps.AddressModels;
using Aiursoft.Pylon.Models.Stargate.ListenAddressModels;
using Aiursoft.Pylon.Services;
using Aiursoft.Pylon.Services.ToStargateServer;
using Kahla.Server.Data;
using Kahla.Server.Models;
using Kahla.Server.Models.ApiAddressModels;
using Kahla.Server.Models.ApiViewModels;
using Kahla.Server.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aiursoft.Pylon.Services.ToAPIServer;
using Kahla.Server.Attributes;
using System.ComponentModel.DataAnnotations;

namespace Kahla.Server.Controllers
{
    // Reject HTTP requests.
    [AiurRequireHttps]

    // Add headers.
    [AiurNoCache]
    [AiurAllowTargetOrigin("https://kahla.app.aiursoft.com")]
    [AllowMethodHeader]

    // Force valid state.
    [ForceValidateModelState]
    [AiurExceptionHandler]
    public class ApiController : AiurApiController
    {
        private readonly UserManager<KahlaUser> _userManager;
        private readonly SignInManager<KahlaUser> _signInManager;
        private readonly KahlaDbContext _dbContext;
        private readonly PushService _pusher;

        public ApiController(UserManager<KahlaUser> userManager,
            SignInManager<KahlaUser> signInManager,
            KahlaDbContext dbContext)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _dbContext = dbContext;
            _pusher = new PushService();
        }
        [HttpPost]
        public async Task<IActionResult> AuthByPassword(AuthByPasswordAddressModel model)
        {
            var pack = await OAuthService.PasswordAuthAsync(Middlewares.CurrentAppId, model.Email, model.Password);
            if (pack.code != ErrorType.Success)
            {
                return Protocal(ErrorType.Unauthorized, pack.message);
            }
            var user = await AuthProcess.AuthApp(this, new AuthResultAddressModel
            {
                code = pack.Value,
                state = string.Empty
            }, _userManager, _signInManager);

            var credential = new Credential
            {
                UserId = user.Id,
                Value = StringOperation.RandomString(30),
            };
            _dbContext.Credentials.Add(credential);
            await _dbContext.SaveChangesAsync();
            return Json(new AiurValue<string>(credential.Value)
            {
                code = ErrorType.Success,
                message = "Auth success."
            });
        }
        [HttpOptions]
        public IActionResult UploadFile(string nomeaning) => NoContent();
        [HttpPost]
        [KahlaRequireCredential]
        public async Task<IActionResult> UploadFile()
        {
            string iconPath = string.Empty;
            if (Request.Form.Files.Count == 0 || Request.Form.Files.First().Length < 1)
            {
                return Protocal(ErrorType.InvalidInput, "You didn't upload any file.");
            }
            else
            {
                var file = Request.Form.Files.First();
                iconPath = await StorageService.SaveToOSS(file, Startup.KahlaBucketId, SaveFileOptions.RandomName);
            }
            return Json(new AiurValue<string>(iconPath)
            {
                code = ErrorType.Success,
                message = "Successfully uploaded your file!"
            });
        }
        [HttpPost]
        public async Task<IActionResult> RegisterKahla(RegisterKahlaAddressModel model)
        {
            var result = await OAuthService.AppRegisterAsync(model.Email, model.Password, model.ConfirmPassword);
            return Json(result);
        }

        public async Task<IActionResult> SignInStatus()
        {
            var user = await GetKahlaUser();
            var signedIn = user != null;
            return Json(new AiurValue<bool>(signedIn)
            {
                code = ErrorType.Success,
                message = "Successfully get your signin status."
            });
        }

        [KahlaRequireCredential]
        public async Task<IActionResult> Me()
        {
            var user = await GetKahlaUser();
            return Json(new AiurValue<KahlaUser>(user)
            {
                code = ErrorType.Success,
                message = "Successfully get your information."
            });
        }

        [KahlaRequireCredential]
        public async Task<IActionResult> MyFriends(bool? orderByName)
        {
            var user = await GetKahlaUser();
            var list = new List<ContactInfo>();
            var conversations = await _dbContext.MyConversations(user.Id);
            foreach (var conversation in conversations)
            {
                list.Add(new ContactInfo
                {
                    ConversationId = conversation.Id,
                    DisplayName = conversation.GetDisplayName(user.Id),
                    DisplayImage = conversation.GetDisplayImage(user.Id),
                    LatestMessage = conversation.GetLatestMessage().Content,
                    LatestMessageTime = conversation.GetLatestMessage().SendTime,
                    UnReadAmount = conversation.GetUnReadAmount(user.Id),
                    Discriminator = conversation.Discriminator,
                    UserId = conversation is PrivateConversation ? (conversation as PrivateConversation).AnotherUser(user.Id).Id : null
                });
            }
            list = orderByName == true ?
                list.OrderBy(t => t.DisplayName).ToList() :
                list.OrderByDescending(t => t.LatestMessageTime).ToList();
            return Json(new AiurCollection<ContactInfo>(list)
            {
                code = ErrorType.Success,
                message = "Successfully get all your friends."
            });
        }
        [HttpOptions]
        public IActionResult DeleteFriend() => NoContent();
        [HttpPost]
        [KahlaRequireCredential]
        public async Task<IActionResult> DeleteFriend(string id)
        {
            var user = await GetKahlaUser();
            var target = await _dbContext.Users.FindAsync(id);
            if (target == null)
                return Protocal(ErrorType.NotFound, "We can not find target user.");
            if (!await _dbContext.AreFriends(user.Id, target.Id))
                return Protocal(ErrorType.NotEnoughResources, "He is not your friend at all.");
            await _dbContext.RemoveFriend(user.Id, target.Id);
            await _dbContext.SaveChangesAsync();
            await _pusher.WereDeletedEvent(target.Id, _dbContext);
            return Protocal(ErrorType.Success, "Successfully deleted your friend relationship.");
        }
        [HttpOptions]
        public IActionResult CreateRequest() => NoContent();
        [HttpPost]
        [KahlaRequireCredential]
        public async Task<IActionResult> CreateRequest(string id)
        {
            var user = await GetKahlaUser();
            var target = await _dbContext.Users.FindAsync(id);
            if (target == null)
                return Protocal(ErrorType.NotFound, "We did not find your target user!");
            if (target.Id == user.Id)
                return Protocal(ErrorType.HasDoneAlready, "You can't request yourself!");
            var pending = _dbContext.Requests
                .Where(t => t.CreatorId == user.Id)
                .Where(t => t.TargetId == id)
                .Exists(t => !t.Completed);
            if (pending)
                return Protocal(ErrorType.HasDoneAlready, "There are some pending request hasn't been completed!");
            var areFriends = await _dbContext.AreFriends(user.Id, target.Id);
            if (areFriends)
                return Protocal(ErrorType.HasDoneAlready, "You two are already friends!");
            var request = new Request { CreatorId = user.Id, TargetId = id };
            _dbContext.Requests.Add(request);
            await _dbContext.SaveChangesAsync();
            await _pusher.NewFriendRequestEvent(target.Id, user.Id, _dbContext);
            return Json(new AiurValue<int>(request.Id)
            {
                code = ErrorType.Success,
                message = "Successfully created your request!"
            });
        }
        [HttpOptions]
        public IActionResult CompleteRequest() => NoContent();
        [HttpPost]
        [KahlaRequireCredential]
        [ForceValidateModelState]
        public async Task<IActionResult> CompleteRequest(CompleteRequestAddressModel model)
        {
            var user = await GetKahlaUser();
            var request = await _dbContext.Requests.FindAsync(model.Id);
            if (request == null)
                return Protocal(ErrorType.NotFound, "We can NOT find target request.");
            if (request.TargetId != user.Id)
                return Protocal(ErrorType.Unauthorized, "The target user of this request is not you.");
            if (request.Completed == true)
                return Protocal(ErrorType.HasDoneAlready, "The target request is already completed.");
            request.Completed = true;
            if (model.Accept)
            {
                if (await _dbContext.AreFriends(request.CreatorId, request.TargetId))
                    return Protocal(ErrorType.HasDoneAlready, "You two are already friends.");
                _dbContext.AddFriend(request.CreatorId, request.TargetId);
                await _pusher.FriendAcceptedEvent(request.CreatorId, _dbContext);
            }
            await _dbContext.SaveChangesAsync();
            return Protocal(ErrorType.Success, "You have successfully completed this request.");
        }

        [KahlaRequireCredential]
        public async Task<IActionResult> MyRequests()
        {
            var user = await GetKahlaUser();
            var requests = await _dbContext
                .Requests
                .AsNoTracking()
                .Include(t => t.Creator)
                .Where(t => t.TargetId == user.Id)
                .OrderByDescending(t => t.CreateTime)
                .ToListAsync();
            return Json(new AiurCollection<Request>(requests)
            {
                code = ErrorType.Success,
                message = "Successfully get your requests list."
            });
        }

        [KahlaRequireCredential]
        [ForceValidateModelState]
        public async Task<IActionResult> SearchFriends(SearchFriendsAddressModel model)
        {
            var users = await _dbContext
                .Users
                .AsNoTracking()
                .Where(t => t.NickName.ToLower().Contains(model.NickName.ToLower()))
                .ToListAsync();

            return Json(new AiurCollection<KahlaUser>(users)
            {
                code = ErrorType.Success,
                message = "Search result is shown."
            });
        }

        [KahlaRequireCredential]
        public async Task<IActionResult> GetMessage(int id, int take = 15)
        {
            var user = await GetKahlaUser();
            var target = await _dbContext.Conversations.FindAsync(id);
            if (!await _dbContext.VerifyJoined(user.Id, target))
                return Protocal(ErrorType.Unauthorized, "You don't have any relationship with that conversation.");
            //Get Messages
            var allMessages = await _dbContext
                .Messages
                .AsNoTracking()
                .Where(t => t.ConversationId == target.Id)
                .Include(t => t.Sender)
                .OrderByDescending(t => t.SendTime)
                .Take(take)
                .OrderBy(t => t.SendTime)
                .ToListAsync();
            if (target.Discriminator == nameof(PrivateConversation))
            {
                await _dbContext.Messages
                    .Where(t => t.ConversationId == target.Id)
                    .Where(t => t.SenderId != user.Id)
                    .Where(t => t.Read == false)
                    .ForEachAsync(t => t.Read = true);
            }
            else if (target.Discriminator == nameof(GroupConversation))
            {
                var relation = await _dbContext.UserGroupRelations
                    .SingleOrDefaultAsync(t => t.UserId == user.Id && t.GroupId == target.Id);
                relation.ReadTimeStamp = DateTime.Now;
            }
            await _dbContext.SaveChangesAsync();
            return Json(new AiurCollection<Message>(allMessages)
            {
                code = ErrorType.Success,
                message = "Successfully get all your messages."
            });
        }
        [HttpOptions]
        public IActionResult SendMessage() => NoContent();
        [HttpPost]
        [KahlaRequireCredential]
        public async Task<IActionResult> SendMessage(SendMessageAddressModel model)
        {
            var user = await GetKahlaUser();
            var target = await _dbContext.Conversations.FindAsync(model.Id);
            if (!await _dbContext.VerifyJoined(user.Id, target))
                return Protocal(ErrorType.Unauthorized, "You don't have any relationship with that conversation.");
            if (model.Content.Trim().Length == 0)
                return Protocal(ErrorType.InvalidInput, "Can not send empty message.");
            //Create message.
            var message = new Message
            {
                Content = model.Content,
                SenderId = user.Id,
                ConversationId = target.Id
            };
            _dbContext.Messages.Add(message);
            await _dbContext.SaveChangesAsync();
            //Push the message to reciever
            if (target.Discriminator == nameof(PrivateConversation))
            {
                var privateConversation = target as PrivateConversation;
                await _pusher.NewMessageEvent(privateConversation.RequesterId, target.Id, _dbContext, model.Content, user);
                await _pusher.NewMessageEvent(privateConversation.TargetId, target.Id, _dbContext, model.Content, user);
            }
            else if (target.Discriminator == nameof(GroupConversation))
            {
                var usersJoined = _dbContext.UserGroupRelations.Where(t => t.GroupId == target.Id);
                await usersJoined.ForEachAsync(async t => await _pusher.NewMessageEvent(t.UserId, target.Id, _dbContext, model.Content, user));
            }
            //Return success message.
            return Protocal(ErrorType.Success, "Your message has been sent.");
        }

        [KahlaRequireCredential]
        public async Task<IActionResult> UserDetail(string id)
        {
            var user = await GetKahlaUser();
            var target = await _dbContext.Users.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id);
            var model = new UserDetailViewModel();
            if (target == null)
            {
                model.message = "We can not find target user.";
                model.code = ErrorType.NotFound;
                return Json(model);
            }
            var conversation = await _dbContext.FindConversation(user.Id, target.Id);
            if (conversation != null)
            {
                model.User = target;
                model.AreFriends = true;
                model.ConversationId = conversation.Id;
                model.message = "Found that user.";
                model.code = ErrorType.Success;
            }
            else
            {
                model.User = target;
                model.AreFriends = false;
                model.ConversationId = 0;
                model.message = "Found that user.";
                model.code = ErrorType.Success;
            }
            return Json(model);
        }

        [KahlaRequireCredential]
        public async Task<IActionResult> ConversationDetail(int id)
        {
            var user = await GetKahlaUser();
            var conversations = await _dbContext.MyConversations(user.Id);
            var target = conversations.SingleOrDefault(t => t.Id == id);
            target.DisplayName = target.GetDisplayName(user.Id);
            target.DisplayImage = target.GetDisplayImage(user.Id);
            if (target is PrivateConversation)
            {
                var pTarget = target as PrivateConversation;
                pTarget.AnotherUserId = pTarget.AnotherUser(user.Id).Id;
                return Json(new AiurValue<Conversation>(pTarget)
                {
                    code = ErrorType.Success,
                    message = "Successfully get target conversation."
                });
            }
            return Json(new AiurValue<Conversation>(target)
            {
                code = ErrorType.Success,
                message = "Successfully get target conversation."
            });
        }

        [KahlaRequireCredential]
        public async Task<IActionResult> InitPusher()
        {
            var user = await GetKahlaUser();
            if (user.CurrentChannel == -1 || (await ChannelService.ValidateChannelAsync(user.CurrentChannel, user.ConnectKey)).code != ErrorType.Success)
            {
                var channel = await _pusher.Init();
                user.CurrentChannel = channel.ChannelId;
                user.ConnectKey = channel.ConnectKey;
                await _userManager.UpdateAsync(user);
            }
            var model = new InitPusherViewModel
            {
                code = ErrorType.Success,
                message = "Successfully get your channel.",
                ChannelId = user.CurrentChannel,
                ConnectKey = user.ConnectKey,
                ServerPath = new AiurUrl(Values.StargateListenAddress, "Listen", "Channel", new ChannelAddressModel
                {
                    Id = user.CurrentChannel,
                    Key = user.ConnectKey
                }).ToString()
            };
            return Json(model);
        }

        public async Task<IActionResult> LogOff()
        {
            await _signInManager.SignOutAsync();
            return Protocal(ErrorType.Success, "Success.");
        }

        private async Task<KahlaUser> GetKahlaUser()
        {
            var exsit = HttpContext.Request.Headers["authorization"].Count() > 0;
            if (!exsit)
                return null;
            var credential = HttpContext.Request.Headers["authorization"].First();
            if (!credential.ToLower().StartsWith("bearer "))
                return null;
            credential = credential.Substring(7);
            return (await this._dbContext.Credentials.Include(t => t.User).SingleOrDefaultAsync(t => t.Value == credential))?.User ?? null;
        }
    }
}
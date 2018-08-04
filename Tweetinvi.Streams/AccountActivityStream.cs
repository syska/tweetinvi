﻿using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Tweetinvi.Core.Events;
using Tweetinvi.Core.Exceptions;
using Tweetinvi.Core.Extensions;
using Tweetinvi.Core.Factories;
using Tweetinvi.Core.Helpers;
using Tweetinvi.Core.Public.Streaming;
using Tweetinvi.Core.Public.Streaming.Events;
using Tweetinvi.Core.Wrappers;
using Tweetinvi.Events;
using Tweetinvi.Logic.DTO;
using Tweetinvi.Logic.DTO.ActivityStream;
using Tweetinvi.Models;
using Tweetinvi.Models.DTO;
using Tweetinvi.Models.Webhooks;
using Tweetinvi.Streams.Model;

namespace Tweetinvi.Streams
{
    public class AccountActivityStream : IAccountActivityStream
    {
        private readonly IJObjectStaticWrapper _jObjectWrapper;
        private readonly IJsonObjectConverter _jsonObjectConverter;
        private readonly ITweetFactory _tweetFactory;
        private readonly IExceptionHandler _exceptionHandler;
        private readonly IUserFactory _userFactory;
        private readonly IMessageFactory _messageFactory;
        private readonly ITwitterCredentials _credentials;
        private readonly Dictionary<string, Action<string, JObject>> _events;

        public AccountActivityStream(
            IExceptionHandler exceptionHandler,
            IJObjectStaticWrapper jObjectWrapper,
            IJsonObjectConverter jsonObjectConverter,
            ITweetFactory tweetFactory,
            IUserFactory userFactory,
            IMessageFactory messageFactory)
        {
            _jObjectWrapper = jObjectWrapper;
            _jsonObjectConverter = jsonObjectConverter;
            _tweetFactory = tweetFactory;
            _exceptionHandler = exceptionHandler;
            _userFactory = userFactory;
            _messageFactory = messageFactory;
            _events = new Dictionary<string, Action<string, JObject>>();

            InitializeEvents();
        }

        private void InitializeEvents()
        {
            _events.Add("tweet_create_events", TryRaiseTweetCreatedEvents);
            _events.Add("favorite_events", TryRaiseTweetFavouritedEvents);
            _events.Add("follow_events", TryRaiseFollowedEvents);
            _events.Add("block_events", TryRaiseUserBlockedEvents);
            _events.Add("mute_events", TryRaiseUserMutedEvents);
            _events.Add("user_event", TryRaiseUserEvent);
            _events.Add("direct_message_events", TryRaiseMessageEvent);
            _events.Add("direct_message_indicate_typing_events", TryRaiseIndicateUserIsTypingMessage);
        }

        public long UserId { get; set; }

        public EventHandler<TweetReceivedEventArgs> TweetCreated { get; set; }
        public EventHandler<TweetFavouritedEventArgs> TweetFavourited { get; set; }
        public EventHandler<UserFollowedEventArgs> UserFollowed { get; set; }
        public EventHandler<UserBlockedEventArgs> UserBlocked { get; set; }
        public EventHandler<UserMutedEventArgs> UserMuted { get; set; }
        public EventHandler<UserRevokedAppPermissionsEventArgs> UserRevokedAppPermissions { get; set; }
        public EventHandler<MessageEventArgs> MessageReceived { get; set; }
        public EventHandler<MessageEventArgs> MessageSent { get; set; }
        public EventHandler<UserIsTypingMessageEventArgs> UserIsTypingMessage { get; set; }

        public EventHandler<UnmanagedMessageReceivedEventArgs> UnmanagedEventReceived { get; set; }
        public EventHandler<JsonObjectEventArgs> JsonObjectReceived { get; set; }


        public void WebhookMessageReceived(IWebhookMessage message)
        {
            var json = message.Json;
            var jsonObjectEvent = _jObjectWrapper.GetJobjectFromJson(json);

            var jsonEventChildren = jsonObjectEvent.Children().ToArray();
            var keys = jsonEventChildren.Where(x => x.Path.EndsWith("event") || x.Path.EndsWith("events"));
            var key = keys.SingleOrDefault();

            if (key == null)
            {
                return;
            }

            this.Raise(JsonObjectReceived, new JsonObjectEventArgs(json));

            var eventName = key.Path;
            if (_events.ContainsKey(eventName))
            {
                _events[eventName].Invoke(eventName, jsonObjectEvent);
            }
            else
            {
                this.Raise(UnmanagedEventReceived, new UnmanagedMessageReceivedEventArgs(json));
            }
        }

        private void TryRaiseTweetCreatedEvents(string eventName, JObject jsonObjectEvent)
        {
            var tweetCreatedEvent = jsonObjectEvent[eventName];
            var tweetCreatedEventJson = tweetCreatedEvent.ToString();
            var tweetDTOs = _jsonObjectConverter.DeserializeObject<ITweetDTO[]>(tweetCreatedEventJson);

            tweetDTOs.ForEach(tweetDTO =>
            {
                var tweet = _tweetFactory.GenerateTweetFromDTO(tweetDTO);
                this.Raise(TweetCreated, new TweetReceivedEventArgs(tweet, "TODO"));
            });
        }

        private void TryRaiseTweetFavouritedEvents(string eventName, JObject jsonObjectEvent)
        {
            var favouriteTweetEvent = jsonObjectEvent[eventName];
            var favouritedTweetEventJson = favouriteTweetEvent.ToString();
            var favouriteEventDTOs = _jsonObjectConverter.DeserializeObject<AccountActivityFavouriteEventDTO[]>(favouritedTweetEventJson);

            favouriteEventDTOs.ForEach(favouriteEventDTO =>
            {
                var tweet = _tweetFactory.GenerateTweetFromDTO(favouriteEventDTO.FavouritedTweet);
                var user = _userFactory.GenerateUserFromDTO(favouriteEventDTO.User);
                this.Raise(TweetFavourited, new TweetFavouritedEventArgs(tweet, "TODO", user));
            });
        }

        private void TryRaiseFollowedEvents(string eventName, JObject jsonObjectEvent)
        {
            var followEvent = jsonObjectEvent[eventName];
            var followedUsers = GetEventTargetUsers(followEvent);

            followedUsers.ForEach(followedUser =>
            {
                this.Raise(UserFollowed, new UserFollowedEventArgs(followedUser, UserId));
            });
        }

        private void TryRaiseUserBlockedEvents(string eventName, JObject jsonObjectEvent)
        {
            var userBlockedEvent = jsonObjectEvent[eventName];
            var blockedUsers = GetEventTargetUsers(userBlockedEvent);

            blockedUsers.ForEach(blockedUser =>
            {
                this.Raise(UserBlocked, new UserBlockedEventArgs(blockedUser, UserId));
            });
        }

        private void TryRaiseUserMutedEvents(string eventName, JObject jsonObjectEvent)
        {
            var userMutedEvent = jsonObjectEvent[eventName];
            var mutedUsers = GetEventTargetUsers(userMutedEvent);

            mutedUsers.ForEach(mutedUser =>
            {
                this.Raise(UserMuted, new UserMutedEventArgs(mutedUser, UserId));
            });
        }

        private void TryRaiseUserEvent(string eventName, JObject jsonObjectEvent)
        {
            var userEvent = jsonObjectEvent[eventName];
            var eventType = userEvent.Children().First().Path;

            if (eventType == "user_event.revoke")
            {
                var json = userEvent["revoke"].ToString();
                var userRevokedAppEventDTO = _jsonObjectConverter.DeserializeObject<IUserRevokedAppPermissionsDTO>(json);
                var userRevokedAppEventArgs = new UserRevokedAppPermissionsEventArgs(userRevokedAppEventDTO);

                this.Raise(UserRevokedAppPermissions, userRevokedAppEventArgs);
            }
            else
            {
                if (!_exceptionHandler.SwallowWebExceptions)
                {
                    throw new ArgumentException($"user_event received of type {eventType} is not supported.");
                }
            }
        }

        private void TryRaiseMessageEvent(string eventName, JObject jsonObjectEvent)
        {
            var messageEvent = jsonObjectEvent[eventName];
            var json = messageEvent.ToString();
            var messageEventDTOs = _jsonObjectConverter.DeserializeObject<IEventDTO[]>(json);
            messageEventDTOs.ForEach(messageEventDTO =>
            {
                var message = _messageFactory.GenerateMessageFromEventDTO(messageEventDTO);

                if (message.SenderId == UserId)
                {
                    this.Raise(MessageSent, new MessageEventArgs(message));
                }
                else
                {
                    this.Raise(MessageReceived, new MessageEventArgs(message));
                }
            });
        }

        private void TryRaiseIndicateUserIsTypingMessage(string eventName, JObject jsonObjectEvent)
        {
            var messageIndicateUserTypingMessageEvent = jsonObjectEvent[eventName];
            var users = jsonObjectEvent["users"].ToObject<Dictionary<long, UserDTO>>();

            var json = messageIndicateUserTypingMessageEvent.ToString();
            var messageIndicateUserTypingMessageEventDTOs = _jsonObjectConverter.DeserializeObject<ActivityStreamDirectMessageIndicateTypingEventDTO[]>(json);

            messageIndicateUserTypingMessageEventDTOs.ForEach(messageIndicateUserTypingMessageEventDTO =>
            {
                var userIsTypingMessageEventArgs = new UserIsTypingMessageEventArgs
                {
                    SenderId = messageIndicateUserTypingMessageEventDTO.SenderId,
                    RecipientId = messageIndicateUserTypingMessageEventDTO.Target.RecipientId
                };

                if (users.TryGetValue(messageIndicateUserTypingMessageEventDTO.SenderId, out var senderDTO))
                {
                    userIsTypingMessageEventArgs.Sender = _userFactory.GenerateUserFromDTO(senderDTO);
                }

                if (users.TryGetValue(messageIndicateUserTypingMessageEventDTO.Target.RecipientId, out var recipientDTO))
                {
                    userIsTypingMessageEventArgs.Recipient = _userFactory.GenerateUserFromDTO(recipientDTO);
                }

                this.Raise(UserIsTypingMessage, userIsTypingMessageEventArgs);
            });
        }


        private IUser[] GetEventTargetUsers(JToken userToUserEvent)
        {
            var userToUserEventJson = userToUserEvent.ToString();
            var userToUserEventDTO = _jsonObjectConverter.DeserializeObject<UserToUserEventDTO[]>(userToUserEventJson);
            var mutedUsers = GetTargetUsersFromUserToUserEvent(userToUserEventDTO);

            return mutedUsers;
        }

        private IUser[] GetTargetUsersFromUserToUserEvent(UserToUserEventDTO[] userToUserEvents)
        {
            return userToUserEvents.Select(x =>
            {
                var source = x.Source;
                var target = x.Target;

                var targetUserDTO = source.Id == UserId ? target : source;
                var targetUser = _userFactory.GenerateUserFromDTO(targetUserDTO);
                return targetUser;
            }).ToArray();
        }
    }
}

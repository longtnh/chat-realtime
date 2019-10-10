﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNet.SignalR;
using Chat.Web.Models.ViewModels;
using Chat.Web.Models;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AutoMapper;

namespace Chat.Web.Hubs
{
    [Authorize]
    public class ChatHub : Hub
    {
        #region Properties
        /// <summary>
        /// List of online users
        /// </summary>
        public readonly static List<UserViewModel> _Connections = new List<UserViewModel>();

        /// <summary>
        /// List of all users
        /// </summary>
        public readonly static List<UserViewModel> _Users = new List<UserViewModel>();

        /// <summary>
        /// List of available chat rooms
        /// </summary>
        private readonly static List<RoomViewModel> _Rooms = new List<RoomViewModel>();

        /// <summary>
        /// Mapping SignalR connections to application users.
        /// (We don't want to share connectionId)
        /// </summary>
        private readonly static Dictionary<string, string> _ConnectionsMap = new Dictionary<string, string>();
        #endregion

        public void Send(string roomName, string fromUserId, string toUserId, string message)
        {
            if(roomName != null)
            {
                SendToRoom(roomName, message);
            }
            else
            {
                SendPrivate(message, fromUserId, toUserId);
            }
        }

        public void SendPrivate(string message, string fromUserId, string toUserId)
        {
            try
            {
                using (var db = new ApplicationDbContext())
                {
                    var userSender = db.Users.Where(u => u.Id == fromUserId).FirstOrDefault();
                    var userReceiver = db.Users.Where(u => u.Id == toUserId).FirstOrDefault();

                    // Create and save message in database
                    Message msg = new Message()
                    {
                        Content = Regex.Replace(message, @"(?i)<(?!img|a|/a|/img).*?>", String.Empty),
                        Timestamp = DateTime.Now.Ticks.ToString(),
                        FromUser = userSender,
                        ToUser = userReceiver,
                    };
                    db.Messages.Add(msg);
                    db.SaveChanges();


                    message = Regex.Replace(message, @"\/private\(.*?\)", string.Empty).Trim();

                    // Build the message
                    MessageViewModel messageViewModel = new MessageViewModel()
                    {
                        From = userSender.DisplayName,
                        Avatar = userSender.Avatar,
                        To = userReceiver.DisplayName,
                        Content = Regex.Replace(message, @"(?i)<(?!img|a|/a|/img).*?>", String.Empty),
                        Timestamp = DateTime.Now.ToLongTimeString()
                    };

                    try
                    {
                        string userId;

                        if (_ConnectionsMap.TryGetValue(userReceiver.UserName, out userId))
                        {
                            // Who is the sender;
                            var sender = _Connections.Where(u => u.Username == IdentityName).First();

                            // Send the message
                            Clients.Client(userId).newMessage(messageViewModel);
                            Clients.Caller.newMessage(messageViewModel);
                        }
                        else
                        {
                            Clients.Caller.newMessage(messageViewModel);
                        }
                    }
                    catch (Exception)
                    {
                        Clients.Caller.newMessage(messageViewModel);
                    }


                }
            }
            catch (Exception)
            {
                Clients.Caller.onError("Message not send!");
            }
            
        }

        public void SendToRoom(string roomName, string message)
        {
            try
            {
                using (var db = new ApplicationDbContext())
                {
                    var user = db.Users.Where(u => u.UserName == IdentityName).FirstOrDefault();
                    var room = db.Rooms.Where(r => r.Name == roomName).FirstOrDefault();

                    // Create and save message in database
                    Message msg = new Message()
                    {
                        Content = Regex.Replace(message, @"(?i)<(?!img|a|/a|/img).*?>", String.Empty),
                        Timestamp = DateTime.Now.Ticks.ToString(),
                        FromUser = user,
                        ToRoom = room
                    };
                    db.Messages.Add(msg);
                    db.SaveChanges();

                    // Broadcast the message
                    var messageViewModel = Mapper.Map<Message, MessageViewModel>(msg);
                    Clients.Group(roomName).newMessage(messageViewModel);
                }
            }
            catch (Exception)
            {
                Clients.Caller.onError("Message not send!");
            }
        }

        public void Join(string roomName)
        {
            try
            {
                var user = _Connections.Where(u => u.Username == IdentityName).FirstOrDefault();
                if (user.CurrentRoom != roomName)
                {
                    // Remove user from others list
                    if (!string.IsNullOrEmpty(user.CurrentRoom))
                        Clients.OthersInGroup(user.CurrentRoom).removeUser(user);

                    // Join to new chat room
                    Leave(user.CurrentRoom);
                    Groups.Add(Context.ConnectionId, roomName);
                    user.CurrentRoom = roomName;

                    // Tell others to update their list of users
                    Clients.OthersInGroup(roomName).addUser(user);
                }
            }
            catch (Exception ex)
            {
                Clients.Caller.onError("You failed to join the chat room!" + ex.Message);
            }
        }

        private void Leave(string roomName)
        {
            Groups.Remove(Context.ConnectionId, roomName);
        }

        public int CreateRoom(string roomName)
        {
            try
            {
                using (var db = new ApplicationDbContext())
                {
                    
                    // Create and save chat room in database
                    var user = db.Users.Where(u => u.UserName == IdentityName).FirstOrDefault();
                    var room = new Room()
                    {
                        Name = roomName,
                        UserAccount = user
                    };
                    var result = db.Rooms.Add(room);
                    db.SaveChanges();
                    room.Id = room.Id;
                    if (room != null)
                    {
                        // Update room list
                        var roomViewModel = Mapper.Map<Room, RoomViewModel>(room);
                        _Rooms.Add(roomViewModel);
                        Clients.All.addChatRoom(roomViewModel);
                    }
                    return room.Id;
                } //using
            }
            catch (Exception ex)
            {
                Clients.Caller.onError("Couldn't create chat room: " + ex.Message);
            }
            return 0;
        }
        public void DeleteRoom(string roomName)
        {
            try
            {
                using (var db = new ApplicationDbContext())
                {
                    // Delete from database
                    var room = db.Rooms.Where(r => r.Name == roomName && r.UserAccount.UserName == IdentityName).FirstOrDefault();
                    db.Rooms.Remove(room);
                    db.SaveChanges();

                    // Delete from list
                    var roomViewModel = _Rooms.First<RoomViewModel>(r => r.Name == roomName);
                    _Rooms.Remove(roomViewModel);

                    // Move users back to Lobby
                    Clients.Group(roomName).onRoomDeleted(string.Format("Room {0} has been deleted.\nYou are now moved to the Lobby!", roomName));

                    // Tell all users to update their room list
                    Clients.All.removeChatRoom(roomViewModel);
                }
            }
            catch (Exception)
            {
                Clients.Caller.onError("Can't delete this chat room.");
            }
        }

        public IEnumerable<MessageViewModel> GetMessageHistory(string roomName, string fromUserId, string toUserId)
        {
            
            using (var db = new ApplicationDbContext())
            {
                if (roomName != null)
                {

                    var messageHistory = db.Messages.Where(m => m.ToRoom.Name == roomName)
                    .OrderByDescending(m => m.Timestamp)
                    .Take(20)
                    .AsEnumerable()
                    .Reverse()
                    .ToList();
                    return Mapper.Map<IEnumerable<Message>, IEnumerable<MessageViewModel>>(messageHistory);
                }
                else if(fromUserId != null && toUserId != null)
                {
                    var messageHistory = db.Messages.Where(m => (m.FromUserId == fromUserId && m.ToUserId == toUserId) || (m.FromUserId == toUserId && m.ToUserId == fromUserId))
                    .OrderByDescending(m => m.Timestamp)
                    .Take(20)
                    .AsEnumerable()
                    .Reverse()
                    .ToList();
                    return Mapper.Map<IEnumerable<Message>, IEnumerable<MessageViewModel>>(messageHistory);
                }
                return null;
            }
        }

        public IEnumerable<UserRoomViewModel> GetRooms(string userId)
        {
            List<UserRoomViewModel> _UsersRoom = new List<UserRoomViewModel>();
            using (var db = new ApplicationDbContext())
            {
                // First run?
                if (_UsersRoom.Count == 0)
                {
                    foreach (UserRoom userRoom in db.UserRooms.ToList())
                    {
                        UserRoomViewModel UserRoomViewModel = Mapper.Map<UserRoom, UserRoomViewModel>(userRoom);
                        _UsersRoom.Add(UserRoomViewModel);
                    }
                }
            }
            return _UsersRoom.Where(u => u.UserId == userId);
        }

        public IEnumerable<UserViewModel> GetOnlineUsers()
        {
            return _Users;
        }

        public IEnumerable<UserRoomViewModel> GetUsersRoom(int roomId)
        {
            List<UserRoomViewModel> _UsersRoom = new List<UserRoomViewModel>();
            using (var db = new ApplicationDbContext())
            {
                // First run?
                if (_UsersRoom.Count == 0)
                {
                    foreach (UserRoom userRoom in db.UserRooms.ToList())
                    {
                        UserRoomViewModel UserRoomViewModel = Mapper.Map< UserRoom ,  UserRoomViewModel  >(userRoom);
                        _UsersRoom.Add(UserRoomViewModel);
                    }
                }
            }
            return _UsersRoom.Where(u => u.RoomId == roomId);
        }
        public void AddUserToRoom(string userId, int roomId)
        {
            try
            {
                using (var db = new ApplicationDbContext())
                {
                    
                    // Create and save chat room in database
                    // var user = db.Users.Where(u => u.UserName == IdentityName).FirstOrDefault();
                    var user = new UserRoom()
                    {
                        UserId = userId,
                        RoomId = roomId,
                    };
                    db.UserRooms.Add(user);
                    db.SaveChanges();

                    //if (room != null)
                    //{
                    //    // Update room list
                    //    var roomViewModel = Mapper.Map<Room, RoomViewModel>(room);
                    //    _Rooms.Add(roomViewModel);
                    //    Clients.All.addChatRoom(roomViewModel);
                    //}
                }//using
            }
            catch (Exception ex)
            {
                Clients.Caller.onError("Couldn't create chat room: " + ex.Message);
            }
        }

        public IEnumerable<UserViewModel> GetAllUsers()
        {
            List<UserViewModel> _Users = new List<UserViewModel>();
            using (var db = new ApplicationDbContext())
            {
                //First run?
                if (_Users.Count == 0)
                {
                    foreach (var user in db.Users)
                    {
                        var userViewModel = Mapper.Map<ApplicationUser, UserViewModel>(user);
                        _Users.Add(userViewModel);
                    }
                }
            }

            return _Users.Where(u => u.Username != IdentityName).ToList();
        }

        #region OnConnected/OnDisconnected
        public override Task OnConnected()
        {
            using (var db = new ApplicationDbContext())
            {
                // First run?
                if (_Users.Count == 0)
                {
                    foreach (ApplicationUser user in db.Users.ToList())
                    {
                        UserViewModel userViewModel = Mapper.Map<ApplicationUser, UserViewModel>(user);
                        _Users.Add(userViewModel);
                    }
                }
            }


            using (var db = new ApplicationDbContext())
            {
                try
                {
                    var user = db.Users.Where(u => u.UserName == IdentityName).FirstOrDefault();

                    var userViewModel = Mapper.Map<ApplicationUser, UserViewModel>(user);
                    userViewModel.Device = GetDevice();
                    userViewModel.CurrentRoom = "";

                    var tempUser = _Users.Where(u => u.Username == IdentityName).FirstOrDefault();
                    _Users.Remove(tempUser);

                    _Users.Add(userViewModel);
                    _Connections.Add(userViewModel);
                    _ConnectionsMap.Add(IdentityName, Context.ConnectionId);

                    Clients.Caller.getProfileInfo(user.Id, user.DisplayName, user.Avatar);
                }
                catch (Exception ex)
                {
                    Clients.Caller.onError("OnConnected:" + ex.Message);
                }
            }

            return base.OnConnected();
        }

        public override Task OnDisconnected(bool stopCalled)
        {
            
            try
            {
                var tempUser = _Users.Where(u => u.Username == IdentityName).FirstOrDefault();
                _Users.Remove(tempUser);

                tempUser.Device = "";
                _Users.Add(tempUser);

                var user = _Connections.Where(u => u.Username == IdentityName).FirstOrDefault();
                _Connections.Remove(user);

                // Tell other users to remove you from their list
                Clients.OthersInGroup(user.CurrentRoom).removeUser(user);

                // Remove mapping
                _ConnectionsMap.Remove(user.Username);
                
            }
            catch (Exception ex)
            {
                Clients.Caller.onError("OnDisconnected: " + ex.Message);
            }

            return base.OnDisconnected(stopCalled);
        }

        public override Task OnReconnected()
        {
            var tempUser = _Users.Where(u => u.Username == IdentityName).FirstOrDefault();
            _Users.Remove(tempUser);

            var user = _Connections.Where(u => u.Username == IdentityName).FirstOrDefault();
            Clients.Caller.getProfileInfo(user.Id, user.DisplayName, user.Avatar);


            _Users.Add(user);
            return base.OnReconnected();
        }
        #endregion

        private string IdentityName
        {
            get { return Context.User.Identity.Name; }
        }

        private string GetDevice()
        {
            string device = Context.Headers.Get("Device");

            if (device != null && (device.Equals("Desktop") || device.Equals("Mobile")))
                return device;

            return "Web";
        }
    }
}
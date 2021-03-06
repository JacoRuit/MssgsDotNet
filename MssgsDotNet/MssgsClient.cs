﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MssgsDotNet.Commands;
using MssgsDotNet.Responses;
using SimpleJson;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using System.Globalization;

namespace MssgsDotNet
{
    public class MssgsClient : IDisposable
    {
        public static readonly string MSSGS_API_URI = "api.mss.gs";
        public static readonly int MSSGS_API_PORT_SSL = 8101;
        public static readonly int MSSGS_API_PORT = 8102;

        public AppCredentials AppCreds { get; private set; }
        public bool IsAuthenticated { get; private set; }

        private Dictionary<string, Func<RawMssgsResponse, MssgsResponse>> factoryMethods;
        private List<Tuple<string, IResponseCallback>> handlers;

        private string name;
        public string Name
        {
            get
            {
                return this.name;
            }
            set
            {
                if (this.InConversation)
                    throw new Exception("Can't change name while in conversation");
                this.name = value;
            }
        }

        public MssgsUser User { get; private set; }

        public int Port { get; private set; }
        public string Host { get; private set; }
        public bool UseSsl
        {
            get
            {
                return this.Port == MSSGS_API_PORT_SSL;
            }
            set
            {
                if (value)
                    this.Port = MSSGS_API_PORT_SSL;
                else
                    this.Port = MSSGS_API_PORT;
            }
        }

        private Queue<string> OutgoingData;
        private PacketBuilder currentPacket;
        private bool stop = false;

        private Thread clientThread;

        public delegate void Handler();
        public delegate void Handler<T>(T obj);
        public delegate void Handler<T, V>(T obj1, V obj2);
        public event Handler Authenticated;
        public event Handler Connected;
        public event Handler<WelcomeMessage> Welcomed;
        public event Handler<MssgsConversation, ConversationInfo> ConversationJoined;
        public event Handler<MssgsConversation, JoinConversationException> ConversationJoinFailed;
        public event Handler<MssgsConversation, MssgsMessage> MessageReceived;
        public event Handler<MssgsConversation, InternalMessage> InternalMessageReceived;
        public event Handler<MssgsConversation> ConversationLeft;
        public event Handler<Exception> ExceptionThrown;
        public event Handler<UsernameInfo> UserAuthed;

        private Dictionary<string, MssgsConversation> conversations;
        public bool InConversation
        {
            get
            {
                return this.conversations.Count > 0;
            }
        }
        public bool IsConnected { get; private set; }
        public bool IsWelcomed { get; private set; }
        public bool IsUserAuthenticated { get; private set; }

        private bool handling;


        public MssgsClient(string mssgsApiHost, int mssgsApiPort, string name)
        {
            this.IsAuthenticated = false;
            this.IsConnected = false;
            this.IsWelcomed = false;
            this.handling = false;
            this.factoryMethods = new Dictionary<string, Func<RawMssgsResponse, MssgsResponse>>();
            this.handlers = new List<Tuple<string, IResponseCallback>>();
            this.conversations = new Dictionary<string, MssgsConversation>();
            this.Name = name;
            this.OutgoingData = new Queue<string>();
            this.Host = mssgsApiHost;
            this.Port = mssgsApiPort;

            this.RegisterResponseFactory<WelcomeMessage>("welcome", (RawMssgsResponse response) =>
                {
                    response.Data.AssureHas("socketAPI");
                    response.Data.AssureHas("webAPI");
                    response.Data.AssureHas("credentials");
                    return new WelcomeMessage(response.Data["credentials"], response.Data["webAPI"], response.Data["socketAPI"]);
                }
            );
            this.RegisterResponseFactory<MessageList>("messages", (RawMssgsResponse rawResponse) =>
                {
                    List<object> messagesraw = (List<object>)SimpleJson.SimpleJson.DeserializeObject(rawResponse["0"]);
                    var messages = new List<MssgsMessage>(); 
                    foreach (object message in messagesraw)
                    {
                        var rawMessage = (IDictionary<string, object>)SimpleJson.SimpleJson.DeserializeObject(message.ToString());
                        var msg = MssgsMessage.Parse(
                            rawMessage.ToDictionary(k => k.Key.ToString(), k => k.Value != null ? k.Value.ToString() : ""),
                            false
                            );
                        messages.Add(msg);
                    }
                    return new MessageList(messages);
                }
            );
          this.RegisterResponseFactory<ResponseTuple<ConversationInfo, UserList>>("conversation", (RawMssgsResponse rawResponse) =>
                {
                    List<object> users = (List<object>)SimpleJson.SimpleJson.DeserializeObject(rawResponse["usernames"]);
                    List<object> ops = (List<object>)SimpleJson.SimpleJson.DeserializeObject(rawResponse["op"]);
                    List<object> globalops = (List<object>)SimpleJson.SimpleJson.DeserializeObject(rawResponse["globalop"]);
                    var userList = new List<MssgsUser>();
                    foreach (object user in users)
                        userList.Add(new MssgsUser((string)user));
                    foreach (object op in ops)
                        userList.Add(new MssgsUser((string)op, true, false));
                    foreach (object globalop in globalops)
                        userList.Add(new MssgsUser((string)globalop, false, true));
                    return new ResponseTuple<ConversationInfo,UserList>(
                        new ConversationInfo
                        {
                            ConversationId = rawResponse["conversation"],
                            Channel = rawResponse["channel"].ToBoolean(),
                            RobotPassword = rawResponse["robotpassword"].ToBoolean(),
                            ReadOnly = rawResponse["readonly"].ToBoolean()
                        },
                        new UserList(userList)
                        );
                }
            );
            this.RegisterResponseFactory<MssgsMessage>("message", (RawMssgsResponse rawResponse) =>
                MssgsMessage.Parse(rawResponse.Data, true)
            );
            this.RegisterResponseFactory<ConversationAction>("join conversation", (RawMssgsResponse rawResponse) =>
                new ConversationAction(ConversationAction.ActionType.UserJoined, rawResponse.Data)
            );
            this.RegisterResponseFactory<ConversationAction>("leave conversation", (RawMssgsResponse rawResponse) =>
                new ConversationAction(ConversationAction.ActionType.UserLeft, rawResponse.Data)
            );
            this.RegisterResponseFactory<ConversationAction>("remove conversation", (RawMssgsResponse rawResponse) =>
                new ConversationAction(ConversationAction.ActionType.Removed, rawResponse.Data)
            );
            this.RegisterResponseFactory<ConversationAction>("conversation name change", (RawMssgsResponse rawResponse) =>
                new ConversationAction(ConversationAction.ActionType.UserChangedName, rawResponse.Data)
            );
            this.RegisterResponseFactory<ConversationAction>("secured conversation", (RawMssgsResponse rawResponse) =>
                new ConversationAction(ConversationAction.ActionType.ConversationSecured, rawResponse.Data)
            );
            this.RegisterResponseFactory<ConversationAction>("unsecured conversation", (RawMssgsResponse rawResponse) =>
                new ConversationAction(ConversationAction.ActionType.ConversationUnsecured, rawResponse.Data)
            );
            this.RegisterResponseFactory<ConversationAction>("new op", (RawMssgsResponse rawResponse) =>
                new ConversationAction(ConversationAction.ActionType.UserOpped, rawResponse.Data)
            );
            this.RegisterResponseFactory<ConversationAction>("new unop", (RawMssgsResponse rawResponse) =>
                new ConversationAction(ConversationAction.ActionType.UserUnopped, rawResponse.Data)
            );
            this.RegisterResponseFactory<MssgsUser>("settings", (RawMssgsResponse rawResponse) =>
                new MssgsUser(rawResponse["username"], rawResponse["op"].ToBoolean(), rawResponse["globalop"].ToBoolean())
            );
            this.RegisterResponseFactory<JoinConversationException>("failed to join conversation", (RawMssgsResponse rawResponse) =>
                new JoinConversationException(rawResponse["message"], Convert.ToInt32(rawResponse["error"]))
            );

            this.RegisterHandler<WelcomeMessage>("welcome", (cid, msg) =>
                {
                    this.IsWelcomed = true;
                    if (this.Welcomed != null)
                        this.Welcomed(msg);
                }
            );
            this.RegisterHandler<ResponseTuple<ConversationInfo, UserList>>("conversation", (cid, tuple) =>
                {
                    var conv = this[cid];
                    foreach (var user in tuple.Item2.Users)
                        conv.AddUser(user);
                    if (this.ConversationJoined != null)
                        this.ConversationJoined(conv, tuple.Item1);
                }
            );
            this.RegisterHandler<MessageList>("messages", (cid, list) =>
                {
                    var conv = this[list.Messages.First().ConversationId];
                    if (this.MessageReceived != null)
                    {
                        foreach (var message in list.Messages)
                        {
                            if (this.InConversation)
                                conv.AddMessage(message);
                            this.MessageReceived(conv, message);
                        }
                    }
                }
            );
            this.RegisterHandler<MssgsMessage>("message", (string cid, MssgsMessage msg) =>
                {
                    var conv = this[cid];
                    if (this.InConversation)
                        conv.AddMessage(msg);
                    if (this.MessageReceived != null)
                        this.MessageReceived(conv, msg);
                    if (msg.Internal && msg.InternalMessage != null && this.InternalMessageReceived != null)
                        this.InternalMessageReceived(conv, msg.InternalMessage);
                }
            );
            this.RegisterHandler<ConversationAction>((cid, action) =>
                {
                    switch (action.Type)
                    {
                        case ConversationAction.ActionType.UserJoined:
                            this[cid].AddUser(
                                new MssgsUser(
                                    action.Data["username"], 
                                    action.Data["op"].ToBoolean(), 
                                    action.Data["globalop"].ToBoolean()
                                    )
                                    );
                            break;
                        case ConversationAction.ActionType.UserLeft:
                            this[cid].RemoveUser(action.Data["username"]);
                            break;
                        case ConversationAction.ActionType.UserChangedName:
                            this[cid].RenameUser(action.Data["oldUsername"], action.Data["username"]);
                            break;
                        case ConversationAction.ActionType.ConversationSecured:
                            this[cid].Password = action.Data["password"];
                            this[cid].Secured = true;
                            break;
                        case ConversationAction.ActionType.ConversationUnsecured:
                            this[cid].Secured = false;
                            break;
                        case ConversationAction.ActionType.UserOpped:
                            this[cid][action.Data["username"]].Op = true;
                            break;
                        case ConversationAction.ActionType.UserUnopped:
                            this[cid][action.Data["username"]].Op = false;
                            break;
                        case ConversationAction.ActionType.Removed:
                            if (this.ConversationLeft != null)
                                this.ConversationLeft(this[cid]);
                            this.Close();
                            break;
                        default:
                            break;
                    }
                }
            );
            this.RegisterHandler<MssgsUser>("settings", (cid, p) => this.User = p );
            this.RegisterHandler<JoinConversationException>("failed to join conversation", (cid, e) =>
                {
                    var conv = this[cid];
                    this.conversations.Remove(cid);
                    if (this.ConversationJoinFailed != null)
                        this.ConversationJoinFailed(conv, e);
                }
            );
        }

       

        public MssgsClient() : this(String.Empty) { }
        public MssgsClient(string name) : this(MssgsClient.MSSGS_API_URI, MssgsClient.MSSGS_API_PORT_SSL, name) { }

        public void Open()
        {
            if (this.IsConnected)
                throw new Exception("This client is already connected to the server!");
            this.clientThread = new Thread(() =>
                {
                    using (TcpClient client = new TcpClient())
                    {
                        client.Connect(this.Host, this.Port);
                        this.IsConnected = true;
                        if (this.Connected != null)
                            this.Connected();
                        Stream stream; 
                        if (this.UseSsl)
                        {
                            var s = new System.Net.Security.SslStream(client.GetStream());
                            s.AuthenticateAsClient(this.Host);
                            stream = s;
                        }
                        else
                        {
                            stream = client.GetStream();
                        }
                        using (StreamWriter writer = new StreamWriter(stream))
                        {
                            Thread readThread = new Thread(() =>
                                {
                                    while (!this.stop && client.Connected)
                                    {
                                        byte[] buffer = new byte[1024];
                                        stream.Read(buffer, 0, buffer.Length);
                                        string incoming = Encoding.UTF8.GetString(buffer);
                                        if (incoming.StartsWith("{") && this.currentPacket == null)
                                            this.currentPacket = new PacketBuilder();
                                        if (this.currentPacket != null)
                                        {
                                            this.currentPacket.Append(incoming);
                                            //Console.WriteLine(">>" + incoming);
                                        }
                                        if (this.currentPacket != null && this.currentPacket.Built())
                                        {
                                            this.HandlePacket(this.currentPacket);
                                            this.currentPacket = null;
                                        }
                                    }
                                }
                                );
                            readThread.Start();
                            while (!this.stop && client.Connected)
                            {
                                if (this.OutgoingData.Count > 0)
                                {
                                    var str = this.OutgoingData.Dequeue();
                                    var data = str + "\r\n\r\n";
                                    //Console.WriteLine("<< " + str);
                                    writer.Write(data);
                                    writer.Flush();
                                }
                            }
                            readThread.Abort();
                        }
                        stream.Close();
                    }
                }
             );
            this.clientThread.Start();
        }

        public void Authenticate(AppCredentials appCreds)
        {
            if (this.IsAuthenticated) return;
            this.ExecuteCommand(new CredentialsCommand(appCreds), (string cid, CredentialsVerification ver) =>
            {
                if (!ver.Valid)
                {
                    this.IsAuthenticated = false;
                    if (this.ExceptionThrown != null)
                        this.ExceptionThrown(new MssgsApiException("Credentials invalid"));
                }
                else
                {
                    this.IsAuthenticated = true;
                    if (this.Authenticated != null)
                        this.Authenticated();
                }
            });
            this.AppCreds = appCreds;
        }

        private void HandlePacket(PacketBuilder packet)
        {
            if (packet.Data == null) return;
            packet.Data.AssureHas("method");
            packet.Data.AssureHas("data");

            IDictionary<string, string> dataDic = null;
            try
            {
                var list = (IDictionary<string, object>)SimpleJson.SimpleJson.DeserializeObject(packet.Data["data"].ToString().Replace("null", "\"\""));
                dataDic = list.ToDictionary(k => k.Key.ToString(), k => k.Value.ToString());
            }
            catch
            {
                dataDic = new Dictionary<string, string>();
                dataDic["0"] = packet.Data["data"].ToString();
            }
            this.DispatchResponse(
                new RawMssgsResponse
                {
                    Method = (string)packet.Data["method"],
                    Data = dataDic
                }
                );
        }

        public void ExecuteCommand<T>(IMssgsCommand<T> command, Action<string, T> callback) where T : MssgsResponse
        {
            if (!this.IsConnected)
                throw new Exception("Please connect first before you execute commands!");
            if (!this.IsWelcomed)
                throw new Exception("The server needs to welcome you first before you can execute commands!");
            this.UnregisterResponseFactory(command.Method);
            this.RegisterResponseFactory<T>(command.Method, command.CreateResponse);
            var jsonObject = new Dictionary<string, object>();
            jsonObject["method"] = command.Method;
            jsonObject["data"] = command.Data;
            jsonObject["end"] = true;
            string json = SimpleJson.SimpleJson.SerializeObject(jsonObject);
            this.OutgoingData.Enqueue(json);
            this.RegisterHandler<T>(command.Method, (string cid, T resp) =>
                {
                    this.UnregisterHandler(command.Method);
                    callback(cid, resp);
                }
            );
        }

        public void ExecuteCommand(IMssgsCommand command)
        {
            if (!this.IsConnected)
                throw new Exception("Please connect first before you execute commands!");
            if (!this.IsWelcomed)
                throw new Exception("The server needs to welcome you first before you can execute commands!");
            var jsonObject = new Dictionary<string, object>();
            jsonObject["method"] = command.Method;
            jsonObject["data"] = command.Data;
            jsonObject["end"] = true;
            string json = SimpleJson.SimpleJson.SerializeObject(jsonObject);
            this.OutgoingData.Enqueue(json);
        }

        private void DispatchResponse(RawMssgsResponse rawResponse)
        {
            if (!this.factoryMethods.ContainsKey(rawResponse.Method))
                return;
            string cid = null;
            if(rawResponse.Data.ContainsKey("conversationId"))
                cid = rawResponse["conversationId"];
            else if (rawResponse.Data.ContainsKey("id"))
                cid = rawResponse["id"];
            else if (rawResponse.Data.ContainsKey("conversation"))
                cid = rawResponse["conversation"];
            var response = this.factoryMethods[rawResponse.Method].Invoke(rawResponse);
            if (response == null) return;
            response.Method = rawResponse.Method;
            this.handling = true;
            foreach (var handler in this.handlers)
            {
                if (handler == null) continue;
                if ((handler.Item1 == null || handler.Item1 == response.Method) && object.Equals(handler.Item2.Type, response.GetType()))
                {
                    handler.Item2.Execute(cid, response);
                }
            }
            this.handling = false;
        }

        public void RegisterResponseFactory<T>(string methodName, Func<RawMssgsResponse, T> factory) where T : MssgsResponse
        {
            Type obj = typeof(T);
            if(this.factoryMethods.ContainsKey(methodName))
                throw new Exception("There is already a factory method registered for response \"" + methodName  + "\"");
            this.factoryMethods[methodName] = factory;
        }

        public void UnregisterResponseFactory(string methodName)
        {
            if(this.factoryMethods.ContainsKey(methodName))
                this.factoryMethods.Remove(methodName);
        }

        public void RegisterHandler<T>(string method, Action<string, T> callback) where T : MssgsResponse
        {
            Task add = new Task(() =>
            {
                while (true)
                {
                    if (!this.handling)
                    {
                        this.handlers.Add(
                            new Tuple<string, IResponseCallback>(method, new ResponseCallback<T>(callback))
                            );
                        return;
                    }
                }
            }
            );
            add.Start();
        }

        public void RegisterHandler<T>(Action<string, T> callback) where T : MssgsResponse
        {
            this.RegisterHandler<T>(null, callback);
        }

        public void UnregisterHandler(string method)
        {
            if (method == null)
                throw new Exception("Please provide a method, it can't be null!");
            Task del = new Task(() =>
            {
                while (true)
                {
                  
                    if (!this.handling)
                    {
                        Tuple<string, IResponseCallback> h = null;
                        foreach (var handler in this.handlers)
                        {
                            if (handler.Item1 == method)
                                h = handler;
                        }
                        if (h != null)
                            this.handlers.Remove(h);
                        return;
                    }
                }
            }
            );
            del.Start();
        }

        
        public void AuthUser(string username,string avatarUrl)
        {
            this.Name = username;
            this.ExecuteCommand(new AuthUserCommand(username, avatarUrl),
                (string cid, UsernameInfo info) =>
                {
                    if (info.Used)
                        if (this.ExceptionThrown != null)
                        {
                            this.ExceptionThrown(new MssgsApiException("This username is already being used!"));
                            return;
                        }
                    if (!info.Valid)
                        if (this.ExceptionThrown != null)
                        {
                            this.ExceptionThrown(new MssgsApiException("This username isn't valid!"));
                            return;
                        }
                    this.Name = info.Username;
                    this.IsUserAuthenticated = true;
                    if (this.UserAuthed != null)
                        this.UserAuthed(info);
                }
            );
        }

        public void Join(string conversationId, string conversationPassword)
        {
            if (this.InConversation)
                throw new Exception("Already in conversation!");
            this.ExecuteCommand(new JoinConversationCommand(conversationId, conversationPassword));
            this.conversations.Add(conversationId, new MssgsConversation(conversationId));
        }

        public void Join(string conversationId)
        {
            this.Join(conversationId, String.Empty);
        }

        public void Send(MssgsMessage message)
        {
            this.ExecuteCommand(new SendMessageCommand(message));
        }

        public void Send(string message, string conversationId)
        {
            if (!this.InConversation)
                throw new Exception("You're not in a conversation!");
            this.Send(new MssgsMessage(message, conversationId));
        }

        public MssgsConversation GetConversation(string id)
        {
            return this.conversations[id];
        }

        public MssgsConversation this[string id]
        {
            get
            {
                return this.GetConversation(id);
            }
        }

        public void Close()
        {
            this.stop = true;
            this.IsConnected = false;
            this.IsAuthenticated = false;
            this.IsWelcomed = false;
            try
            {
                this.clientThread.Abort();
            }
            catch { }
        }

        public void Dispose()
        {
            this.Close();
            GC.SuppressFinalize(this);
        }

    }

    class ResponseCallback<T> : IResponseCallback where T : MssgsResponse
    {
        public Type Type { get; set; }
        private Action<string, T> func;

        public ResponseCallback(Action<string, T> func)
        {
            this.Type = typeof(T);
            this.func = func;
        }

        public void Execute(string arg1, MssgsResponse arg2)
        {
            this.func.Invoke(arg1, (T)arg2);
        }
    }

    interface IResponseCallback
    {
        Type Type { get;  set; }

        void Execute(string arg1, MssgsResponse arg2);
    }
}

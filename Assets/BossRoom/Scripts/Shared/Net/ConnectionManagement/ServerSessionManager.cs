using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Assertions;

namespace Unity.Multiplayer.Samples.BossRoom.Server
{
    public struct SessionPlayerData
    {
        public ulong ClientID;
        public string ClientGUID;
        public string PlayerName;
        public Vector3 PlayerPosition;
        public Vector3 PlayerRotation;
        public NetworkGuid AvatarNetworkGuid;
        public bool IsConnected;
        public bool IsReconnecting;

        public SessionPlayerData(ulong clientID, string clientGUID, string name, Vector3 position, Vector3 rotation, NetworkGuid avatarNetworkGuid, bool isConnected = false, bool isReconnecting = false)
        {
            ClientID = clientID;
            ClientGUID = clientGUID;
            PlayerName = name;
            PlayerPosition = position;
            PlayerRotation = rotation;
            AvatarNetworkGuid = avatarNetworkGuid;
            IsConnected = isConnected;
            IsReconnecting = isReconnecting;
        }

        public SessionPlayerData(ulong clientID, string clientGUID, string name, Vector3 position, Vector3 rotation, bool isConnected = false, bool isReconnecting = false)
        {
            ClientID = clientID;
            ClientGUID = clientGUID;
            PlayerName = name;
            PlayerPosition = position;
            PlayerRotation = rotation;
            AvatarNetworkGuid = new NetworkGuid();
            IsConnected = isConnected;
            IsReconnecting = isReconnecting;
        }
    }

    public class ServerSessionManager : MonoBehaviour
    {
        private static ServerSessionManager _instance;

        public static ServerSessionManager Instance
        {
            get { return _instance; }
        }

        public GameNetPortal m_Portal;

        /// <summary>
        /// Maps a given client guid to the data for a given client player.
        /// </summary>
        private Dictionary<string, SessionPlayerData> m_ClientData;

        /// <summary>
        /// Map to allow us to cheaply map from guid to player data.
        /// </summary>
        private Dictionary<ulong, string> m_ClientIDToGuid;

        // used in ApprovalCheck. This is intended as a bit of light protection against DOS attacks that rely on sending silly big buffers of garbage.
        private const int k_MaxConnectPayload = 1024;

        private Vector3 m_InitialPositionRotation = Vector3.zero;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(this.gameObject);
            }
            else
            {
                _instance = this;
            }
        }

        void Start()
        {
            if (m_Portal == null) return;

            //m_Portal.NetworkReadied += OnNetworkReady;

            // we add ApprovalCheck callback BEFORE OnNetworkSpawn to avoid spurious NGO warning:
            // "No ConnectionApproval callback defined. Connection approval will timeout"
            m_Portal.NetManager.OnServerStarted += ServerStartedHandler;
            m_ClientData = new Dictionary<string, SessionPlayerData>();
            m_ClientIDToGuid = new Dictionary<ulong, string>();

            DontDestroyOnLoad(this);
        }

        void OnDestroy()
        {
            if (m_Portal != null)
            {
                //m_Portal.NetworkReadied -= OnNetworkReady;

                if (m_Portal.NetManager != null)
                {
                    m_Portal.NetManager.OnServerStarted -= ServerStartedHandler;
                }
            }
        }

        /// <summary>
        /// Handles the case where NetworkManager has told us a client has disconnected. This includes ourselves, if we're the host,
        /// and the server is stopped."
        /// </summary>
        private void OnClientDisconnect(ulong clientId)
        {
            if (m_ClientIDToGuid.TryGetValue(clientId, out var guid))
            {
                if (m_ClientData[guid].ClientID == clientId)
                {
                    var sessionPlayerData = m_ClientData[guid];
                    sessionPlayerData.IsConnected = false;
                    sessionPlayerData.IsReconnecting = false;
                    m_ClientData[guid] = sessionPlayerData;
                    // Wait a few seconds before removing the clientId-GUID mapping to allow for final updates to SessionPlayerData.
                    StartCoroutine(WaitToRemoveClientId(clientId));
                }
            }

            if (clientId == m_Portal.NetManager.LocalClientId)
            {
                //the ServerSessionManager may be initialized again, which will cause its OnNetworkSpawn to be called again.
                //Consequently we need to unregister anything we registered, when the NetworkManager is shutting down.
                m_Portal.NetManager.OnClientDisconnectCallback -= OnClientDisconnect;
            }
        }

        private IEnumerator WaitToRemoveClientId(ulong clientId)
        {
            yield return new WaitForSeconds(5.0f);
            m_ClientIDToGuid.Remove(clientId);
        }


        /// <summary>
        /// Handles the flow when a user has requested a disconnect via UI (which can be invoked on the Host, and thus must be
        /// handled in server code).
        /// </summary>
        public void OnUserDisconnectRequest()
        {
            Clear();
        }

        private void Clear()
        {
            //resets all our runtime state.
            m_ClientData.Clear();
            m_ClientIDToGuid.Clear();
        }

        public bool IsServerFull()
        {
            return m_ClientData.Count >= CharSelectData.k_MaxLobbyPlayers;
        }

        /// <summary>
        /// Handles the flow when a user is connecting by testing for duplicate logins and populating the session's data.
        /// Invoked during the approval check and returns the connection status.
        /// </summary>
        /// <param name="clientId">This is the clientId that Netcode assigned us on login. It does not persist across multiple logins from the same client. </param>
        /// <param name="clientGUID">This is the clientGUID that is unique to this client and persists accross multiple logins from the same client</param>
        /// <param name="playerName">The player's name</param>
        /// <returns></returns>
        public ConnectStatus OnClientApprovalCheck(ulong clientId, string clientGUID, string playerName)
        {
            ConnectStatus gameReturnStatus = ConnectStatus.Success;
            SessionPlayerData sessionPlayerData = new SessionPlayerData(clientId, clientGUID, playerName, m_InitialPositionRotation, m_InitialPositionRotation, true);
            //Test for Duplicate Login.
            if (m_ClientData.ContainsKey(clientGUID))
            {
                if (m_ClientData[clientGUID].IsConnected)
                {
                    if (Debug.isDebugBuild)
                    {
                        Debug.Log($"Client GUID {clientGUID} already exists. Because this is a debug build, we will still accept the connection");
                        while (m_ClientData.ContainsKey(clientGUID)) { clientGUID += "_Secondary"; }
                    }
                    else
                    {
                        gameReturnStatus = ConnectStatus.LoggedInAgain;
                    }
                }
                else
                {
                    // Reconnecting. Give data from old player to new player

                    // Update player session data
                    sessionPlayerData = m_ClientData[clientGUID];
                    sessionPlayerData.ClientID = clientId;
                    sessionPlayerData.IsConnected = true;
                    sessionPlayerData.IsReconnecting = true;
                }

            }

            //Populate our dictionaries with the SessionPlayerData
            if (gameReturnStatus == ConnectStatus.Success)
            {
                m_ClientIDToGuid[clientId] = clientGUID;
                m_ClientData[clientGUID] = sessionPlayerData;
            }

            return gameReturnStatus;
        }

        /// <summary>
        /// Handles the flow when a user's connection is approved. Assigns name and class to persistent player.
        /// Invoked after the approval check.
        /// </summary>
        /// <param name="clientId">This is the clientId that Netcode assigned us on login. It does not persist across multiple logins from the same client. </param>
        /// <param name="clientGUID">This is the clientGUID that is unique to this client and persists accross multiple logins from the same client</param>
        public void OnConnectionApproved(ulong clientId, string clientGUID)
        {
            SessionPlayerData? sessionPlayerData = GetPlayerData(clientGUID);
            Assert.IsTrue(sessionPlayerData.HasValue, $"SessionPlayerData not found for client GUID!");
            AssignPlayerName(clientId, sessionPlayerData.Value.PlayerName);
            AssignPlayerAvatar(clientId, sessionPlayerData.Value.AvatarNetworkGuid);
        }


        public string GetPlayerGUID(ulong clientID)
        {
            return m_ClientIDToGuid[clientID];
        }


        /// <summary>
        ///
        /// </summary>
        /// <param name="clientId"> id of the client whose data is requested</param>
        /// <returns>Player data struct matching the given ID</returns>
        public SessionPlayerData? GetPlayerData(ulong clientId)
        {
            //First see if we have a guid matching the clientID given.

            if (m_ClientIDToGuid.TryGetValue(clientId, out string clientguid))
            {
                if (m_ClientData.TryGetValue(clientguid, out SessionPlayerData data))
                {
                    return data;
                }
                else
                {
                    Debug.Log("No PlayerData of matching guid found");
                }
            }
            else
            {
                Debug.Log("No client guid found mapped to the given client ID");
            }
            return null;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="clientGUID"> guid of the client whose data is requested</param>
        /// <returns>Player data struct matching the given ID</returns>
        public SessionPlayerData? GetPlayerData(string clientGUID)
        {
            //First see if we have a guid matching the clientID given.


            if (m_ClientData.TryGetValue(clientGUID, out SessionPlayerData data))
            {
                return data;
            }
            else
            {
                Debug.Log("No PlayerData of matching guid found");
            }
            return null;
        }

        /// <summary>
        /// Convenience method to get player name from player data
        /// Returns name in data or default name using playerNum
        /// </summary>
        public string GetPlayerName(ulong clientId, int playerNum)
        {
            var playerData = GetPlayerData(clientId);
            return (playerData != null) ? playerData.Value.PlayerName : ("Player" + playerNum);
        }

        /// <summary>
        /// Called after the server is created-  This is primarily meant for the host server to clean up or handle/set state as its starting up
        /// </summary>
        private void ServerStartedHandler()
        {
            if (!m_Portal.NetManager.IsServer)
            {
                enabled = false;
            }
            else
            {
                //O__O if adding any event registrations here, please add an unregistration in OnClientDisconnect.
                //m_Portal.UserDisconnectRequested += OnUserDisconnectRequest;
                m_Portal.NetManager.OnClientDisconnectCallback += OnClientDisconnect;

                m_ClientData.Add("host_guid", new SessionPlayerData(m_Portal.NetManager.LocalClientId, "host_guid", m_Portal.PlayerName, m_InitialPositionRotation, m_InitialPositionRotation, true));
                m_ClientIDToGuid.Add(m_Portal.NetManager.LocalClientId, "host_guid");

                AssignPlayerName(NetworkManager.Singleton.LocalClientId, m_Portal.PlayerName);
            }
        }

        /// <summary>
        /// Updates the player's session data.
        /// </summary>
        /// <param name="clientId">Id of the client to update</param>
        /// <param name="playerName">The player's name to save</param>
        /// <param name="avatarNetworkGuid">The NetworkGuid describing the player's class</param>
        public void UpdatePlayerData(ulong clientId, string playerName, NetworkGuid avatarNetworkGuid)
        {
            if (m_ClientIDToGuid.TryGetValue(clientId, out var guid))
            {
                if (m_ClientData.TryGetValue(guid, out var sessionPlayerData))
                {
                    sessionPlayerData.PlayerName = playerName;
                    sessionPlayerData.AvatarNetworkGuid = avatarNetworkGuid;
                    m_ClientData[guid] = sessionPlayerData;
                }
            }
        }

        public void UpdatePlayerTransform(ulong clientId, Vector3 position, Vector3 rotation)
        {
            if (m_ClientIDToGuid.TryGetValue(clientId, out var guid))
            {
                if (m_ClientData.TryGetValue(guid, out var sessionPlayerData))
                {
                    sessionPlayerData.PlayerPosition = position;
                    sessionPlayerData.PlayerRotation = rotation;
                    m_ClientData[guid] = sessionPlayerData;
                }
            }
        }

        static void AssignPlayerName(ulong clientId, string playerName)
        {
            // get this client's player NetworkObject
            var networkObject = NetworkManager.Singleton.SpawnManager.GetPlayerNetworkObject(clientId);

            // update client's name
            if (networkObject.TryGetComponent(out PersistentPlayer persistentPlayer))
            {
                persistentPlayer.NetworkNameState.Name.Value = playerName;
            }
        }

        static void AssignPlayerAvatar(ulong clientId, NetworkGuid avatarNetworkGuid)
        {
            // get this client's player NetworkObject
            var networkObject = NetworkManager.Singleton.SpawnManager.GetPlayerNetworkObject(clientId);

            // update client's player avatar
            if (networkObject.TryGetComponent(out PersistentPlayer persistentPlayer))
            {
                persistentPlayer.NetworkAvatarGuidState.AvatarGuid.Value = avatarNetworkGuid;
            }
        }
    }
}
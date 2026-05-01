using UnityEngine;
using Mirror;
using RPG.UI;
using RPG.Character;

namespace RPG.Network
{
    /// <summary>
    /// NetworkUIConnector — conecta o UIManager ao player local depois do spawn.
    /// Coloque este script no mesmo objeto que o UIManager na cena.
    /// </summary>
    public class NetworkUIConnector : MonoBehaviour
    {
        private bool _connected;

        private void Update()
        {
            if (_connected) return;
            if (!NetworkClient.active) return;
            if (NetworkClient.localPlayer == null) return;

            var playerEntity = NetworkClient.localPlayer.GetComponent<PlayerEntity>();
            if (playerEntity == null) return;

            UIManager.Instance?.BindLocalPlayer(playerEntity);
            _connected = true;
            Debug.Log("[NetworkUIConnector] HUD conectado ao player local.");
        }
    }
}

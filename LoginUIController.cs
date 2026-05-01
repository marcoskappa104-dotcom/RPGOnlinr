using UnityEngine;
using Mirror;
using RPG.Managers;

namespace RPG.Network
{
    /// <summary>
    /// NetworkGameplayBootstrapper — detecta o modo automaticamente:
    ///
    ///   Build com "-batchmode" (Server Build) → StartServer()
    ///   Build normal (jogador)                → StartClient() conectando em serverAddress
    ///
    /// O jogador NUNCA digita IP. O IP fica fixo aqui no Inspector.
    /// Para produção: troque "localhost" pelo IP/domínio do servidor.
    /// </summary>
    public class NetworkGameplayBootstrapper : MonoBehaviour
    {
        [Header("Endereço do Servidor")]
        [Tooltip("Para testes locais: localhost\nPara produção: IP ou domínio do servidor")]
        [SerializeField] private string serverAddress = "localhost";
        [SerializeField] private ushort serverPort    = 7777;

        private void Start()
        {
            // Configura o transporte com a porta correta
            var kcp = FindObjectOfType<kcp2k.KcpTransport>();
            if (kcp != null) kcp.Port = serverPort;

            if (IsServerBuild())
            {
                // ── SERVIDOR DEDICADO ──────────────────────────────────────
                // Roda headless, sem gráficos, sem player local.
                // Ativado automaticamente quando o build tem -batchmode.
                Debug.Log($"[Network] === SERVIDOR DEDICADO === porta {serverPort}");
                NetworkManager.singleton.StartServer();
            }
            else
            {
                // ── CLIENTE (JOGADOR) ──────────────────────────────────────
                // Verifica se tem personagem selecionado
                if (GameManager.Instance?.SelectedCharacter == null)
                {
                    Debug.LogWarning("[Network] Nenhum personagem selecionado. Voltando ao login.");
                    UnityEngine.SceneManagement.SceneManager.LoadScene(GameManager.SCENE_LOGIN);
                    return;
                }

                Debug.Log($"[Network] Conectando ao servidor: {serverAddress}:{serverPort}");
                NetworkManager.singleton.networkAddress = serverAddress;
                NetworkManager.singleton.StartClient();
            }
        }

        /// <summary>
        /// Retorna true quando o build foi marcado como "Server Build" no Unity.
        /// Application.isBatchMode = true em builds headless.
        /// </summary>
        private bool IsServerBuild()
        {
            return Application.isBatchMode;
        }

        private void OnDestroy()
        {
            if (NetworkServer.active && NetworkClient.isConnected)
                NetworkManager.singleton?.StopHost();
            else if (NetworkClient.isConnected)
                NetworkManager.singleton?.StopClient();
            else if (NetworkServer.active)
                NetworkManager.singleton?.StopServer();
        }
    }
}

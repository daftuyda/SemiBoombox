using Photon.Pun;
using UnityEngine;
using System.Collections.Generic;
using System.Threading.Tasks;
using SemiBoombox.Utils;
using UnityEngine.Networking;
using System;
using System.IO;

namespace SemiBoombox
{
    public class Boombox : MonoBehaviour
    {
        public PhotonView photonView;
        public AudioSource audioSource;

        // Caches the AudioClips in memory using the URL as the key.
        private static Dictionary<string, AudioClip> downloadedClips = new Dictionary<string, AudioClip>();

        // Tracks which players have reported they are ready.
        private static Dictionary<string, HashSet<int>> downloadsReady = new Dictionary<string, HashSet<int>>();

        private void Awake()
        {
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.spatialBlend = 1f;
            audioSource.playOnAwake = false;
            audioSource.minDistance = 1f;
            audioSource.maxDistance = 30f;
            audioSource.rolloffMode = AudioRolloffMode.Custom;
            
            AnimationCurve curve = new();
            curve.AddKey(1f, 1f);
            curve.AddKey(30f, 0f);
            audioSource.SetCustomCurve(AudioSourceCurveType.CustomRolloff, curve);

            photonView = GetComponent<PhotonView>();

            // check if the photoView is working
            if (photonView == null)
            {
                Debug.LogError("PhotonView not found on Boombox object.");
                return;
            }

            if (photonView.IsMine)
            {
                audioSource.volume = 0.1f;
                gameObject.AddComponent<BoomboxUI>();
            }
            else
            {
                audioSource.volume = 0.20f;
            }
        }

        [PunRPC]
        public async void RequestSong(string url, int requesterId)
        {
            Debug.Log($"RequestSong RPC received: url={url}, requesterId={requesterId}");

            if (!downloadedClips.ContainsKey(url))
            {
                try
                {
                    string filePath = await YoutubeDL.DownloadAudioAsync(url);

                    AudioClip clip = await GetAudioClipAsync(filePath);

                    downloadedClips[url] = clip;
                    Debug.Log($"Downloaded and cached clip for url: {url}");
                }
                catch(Exception ex)
                {
                    Debug.LogError($"Failed to download audio: {ex.Message}");
                    return;
                }
            }
            else
            {
                Debug.Log($"Clip already cached for url: {url}");
            }

            photonView.RPC("ReportDownloadComplete", RpcTarget.All, PhotonNetwork.LocalPlayer.ActorNumber, url);
            await WaitForAllPlayersReady(url);
            photonView.RPC("SyncPlayback", RpcTarget.All, url, requesterId);
        }

        [PunRPC]
        public void ReportDownloadComplete(int actorNumber, string url)
        {
            if (!downloadsReady.ContainsKey(url))
                downloadsReady[url] = new HashSet<int>();

            downloadsReady[url].Add(actorNumber);
            Debug.Log($"Player {actorNumber} reported ready for url: {url}. Total ready: {downloadsReady[url].Count}");
        }

        private async Task WaitForAllPlayersReady(string url)
        {
            int totalPlayers = PhotonNetwork.PlayerList.Length;
            while (!downloadsReady.ContainsKey(url) || downloadsReady[url].Count < totalPlayers)
            {
                await Task.Delay(100);
            }
        }

        [PunRPC]
        public void SyncPlayback(string url, int requesterId)
        {
            Debug.Log($"SyncPlayback RPC received: url={url}, requesterId={requesterId}");

            Boombox targetBoombox = FindBoomboxForPlayer(requesterId);
            if (targetBoombox == null)
            {
                Debug.LogError($"No boombox found for player {requesterId}");
                return;
            }

            if (!downloadedClips.ContainsKey(url))
            {
                Debug.LogError($"Clip not found for url: {url}");
                return;
            }

            targetBoombox.audioSource.clip = downloadedClips[url];
            targetBoombox.audioSource.Play();
        }

        [PunRPC]
        public void StopPlayback(int requesterId)
        {
            if (photonView.Owner != null && photonView.Owner.ActorNumber == requesterId)
            {
                Debug.Log($"Stopping playback on Boombox owned by player {requesterId}");
                
                if (audioSource.isPlaying)
                {
                    audioSource.Stop();
                }
            }
        }

        private static Boombox FindBoomboxForPlayer(int playerId)
        {
            foreach (Boombox boombox in FindObjectsOfType<Boombox>())
            {
                if (boombox.photonView.Owner != null && boombox.photonView.Owner.ActorNumber == playerId)
                {
                    return boombox;
                }
            }
            return null;
        }

        public static async Task<AudioClip> GetAudioClipAsync(string filePath)
        {
            string uri = "file://" + filePath;
            using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.MPEG))
            {
                TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
                var operation = www.SendWebRequest();
                operation.completed += (asyncOp) =>
                {
                    if (www.result != UnityWebRequest.Result.Success)
                        tcs.SetException(new Exception($"Failed to load audio file: {www.error}"));
                    else
                        tcs.SetResult(true);
                };
                await tcs.Task;
                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);

                if (Directory.Exists(Path.GetDirectoryName(filePath)))
                {
                    Directory.Delete(Path.GetDirectoryName(filePath), true);
                }

                return clip;
            }
        }
    }
}
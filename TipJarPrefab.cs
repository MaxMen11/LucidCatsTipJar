using System.Reflection;
using Unity.Netcode;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LucidCats.TipJar
{
	internal sealed class TipJarPrefab : INetworkPrefabInstanceHandler
	{
		private const string StableId = "LucidCats.TipJar.NetworkPrefab.v1";

		private static readonly FieldInfo GlobalObjectIdHashField = typeof(NetworkObject).GetField(
			"GlobalObjectIdHash", BindingFlags.NonPublic | BindingFlags.Instance);

		public GameObject Source { get; }

		private TipJarPrefab(GameObject source)
		{
			Source = source;
		}

		public static TipJarPrefab CreateAndRegister(NetworkManager networkManager)
		{
			GameObject source = TipJarInteractable.BuildPrefab();
			source.SetActive(false);
			Object.DontDestroyOnLoad(source);

			// Stable FNV-1a hash of a fixed id, so every client agrees on the prefab.
			// https://stackoverflow.com/questions/13974443/c-sharp-implementation-of-fnv-hash
			uint hash;
			unchecked
			{
				hash = 2166136261u;

				foreach (byte b in System.Text.Encoding.UTF8.GetBytes(StableId))
				{
					hash ^= b;
					hash *= 16777619u;
				}

				if (hash == 0u)
					hash = 1u;
			}

			TipJarPrefab prefab = new TipJarPrefab(source);
			GlobalObjectIdHashField.SetValue(source.GetComponent<NetworkObject>(), hash);

			networkManager.AddNetworkPrefab(source);
			networkManager.PrefabHandler.AddHandler(source, prefab);

			return prefab;
		}

		public NetworkObject Instantiate(Vector3 position, Quaternion rotation)
		{
			GameObject clone = Object.Instantiate(Source, position, rotation);
			clone.SetActive(true);
			return clone.GetComponent<NetworkObject>();
		}

		NetworkObject INetworkPrefabInstanceHandler.Instantiate(ulong ownerClientId, Vector3 position, Quaternion rotation)
			=> Instantiate(position, rotation);

		void INetworkPrefabInstanceHandler.Destroy(NetworkObject networkObject)
		{
			if (networkObject != null)
				Object.Destroy(networkObject.gameObject);
		}
	}
}
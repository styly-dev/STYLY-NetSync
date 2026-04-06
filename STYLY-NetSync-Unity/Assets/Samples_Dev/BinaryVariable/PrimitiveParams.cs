using UnityEngine;

namespace Styly.NetSync.Samples.BinaryVariable
{
    /// <summary>
    /// ScriptableObject that holds size and color for three primitives.
    /// Serialized to byte[] via JsonUtility for NetworkVariable sync.
    /// </summary>
    [CreateAssetMenu(fileName = "PrimitiveParams", menuName = "STYLY NetSync/Samples/PrimitiveParams")]
    public class PrimitiveParams : ScriptableObject
    {
        [System.Serializable]
        public struct Entry
        {
            public float size;
            public Color color;
        }

        public Entry primitive0 = new Entry { size = 1f, color = Color.red };
        public Entry primitive1 = new Entry { size = 1f, color = Color.green };
        public Entry primitive2 = new Entry { size = 1f, color = Color.blue };

        public byte[] Serialize()
        {
            string json = JsonUtility.ToJson(this);
            return System.Text.Encoding.UTF8.GetBytes(json);
        }

        public void Deserialize(byte[] data)
        {
            string json = System.Text.Encoding.UTF8.GetString(data);
            JsonUtility.FromJsonOverwrite(json, this);
        }
    }
}

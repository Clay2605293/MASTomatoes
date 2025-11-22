using UnityEngine;

public class TileInfo : MonoBehaviour
{
    [Tooltip("¿Se puede caminar sobre este tile?")]
    public bool walkable = true;

    // Posición en coordenadas de grilla (x,z)
    [HideInInspector] public Vector2Int gridPos;
}

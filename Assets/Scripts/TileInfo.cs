using UnityEngine;  

public class TileInfo : MonoBehaviour
{
    // Posición de este tile en el grid lógico
    public Vector2Int gridPos;

    // Si se puede caminar encima (pasillo) o no (cama de cultivo)
    public bool walkable = true;

    [Header("Tomates / Cosecha")]
    // Tile donde se para el bot para cosechar
    public bool isHarvestSpot = false;

    // Tile de cama que tiene planta (dirt con tomate)
    public bool hasPlant = false;
}

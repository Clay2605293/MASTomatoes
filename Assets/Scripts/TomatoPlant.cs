using UnityEngine;

public class TomatoPlant : MonoBehaviour
{
    [Header("Tomates en esta planta")]
    [Range(1, 5)]
    public int tomatoes = 3;      // se puede randomizar al inicio

    [HideInInspector]
    public Vector2Int gridPos;

    private void Start()
    {
        // Registrar su posición de grilla al iniciar
        var grid = GridManager.Instance;
        gridPos = grid.WorldToGrid(transform.position);
    }

    public bool HasTomatoes => tomatoes > 0;

    public int TakeTomatoes(int maxToTake)
    {
        int toTake = Mathf.Min(maxToTake, tomatoes);
        tomatoes -= toTake;
        return toTake;
    }
}

using UnityEngine;

public class PositionToHomeTile : MonoBehaviour
{
    public GameObject homeTile;

    void Start()
    {
        if (homeTile != null)
        {
            Vector3 newPosition = transform.position;
            
            newPosition.x = homeTile.transform.position.x;
            newPosition.z = homeTile.transform.position.z;
            
            transform.position = newPosition;
        }
        else
        {
            Debug.LogWarning("homeTile not assigned in Inspector!");
        }
    }
}
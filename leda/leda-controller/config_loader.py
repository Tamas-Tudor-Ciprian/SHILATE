import json
import os

def load_ray_count_from_config(config_path="../../config.json", fallback=9):
    """
    Loads the ray_count value from the config.json file.
    Returns fallback if file or value is missing/invalid.
    """
    try:
        with open(os.path.abspath(config_path), "r") as f:
            config = json.load(f)
        return int(config.get("model", {}).get("ray_count", fallback))
    except Exception:
        return fallback

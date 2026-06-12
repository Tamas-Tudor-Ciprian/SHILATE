import json
import os

def load_ray_count_from_config(config_path="../../config.json"):
    """
    Loads the ray_count value from config.json.
    Raises if the file is missing, malformed, or the key is absent.
    """
    with open(os.path.abspath(config_path), "r") as f:
        config = json.load(f)
    return int(config["model"]["ray_count"])

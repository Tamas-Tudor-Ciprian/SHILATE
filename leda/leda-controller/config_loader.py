import json
import os

def load_ray_count_from_config(config_path="../../config.json"):
    """
    Loads the ray_count value. RAY_COUNT env var takes priority (used in
    containers where config.json is not in the image). Falls back to reading
    config.json; raises if neither is available.
    """
    env_val = os.environ.get("RAY_COUNT")
    if env_val is not None:
        return int(env_val)
    with open(os.path.abspath(config_path), "r") as f:
        config = json.load(f)
    return int(config["model"]["ray_count"])

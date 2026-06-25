"""
SHILATE RL Model Configuration — thin wrapper around SB3 PPO defaults.

Customize policy_kwargs here if you want to change the neural net architecture.
"""


def get_policy_kwargs(ray_count: int):
    """
    Returns policy_kwargs for Stable-Baselines3 PPO.

    Obs layout (ray_count + 8 total):
      rays(N) | speed | steer | progress | vForward | vLateral
              | prev_steer | prev_throttle | prev_brake
    SB3 reads the actual shape from the environment; obs_size here is for reference only.
    """
    obs_size = ray_count + 8  # noqa: F841 — reference only

    # Separate actor (pi) and critic (vf) networks — each has its own independent weights.
    return {
        "net_arch": dict(pi=[256, 256], vf=[256, 256]),
    }

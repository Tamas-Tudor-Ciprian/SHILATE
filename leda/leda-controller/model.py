"""
SHILATE RL Model Configuration — thin wrapper around SB3 PPO defaults.

Customize policy_kwargs here if you want to change the neural net architecture.
"""


def get_policy_kwargs(ray_count: int = 9):
    """
    Returns policy_kwargs for Stable-Baselines3 PPO.

    Default MlpPolicy uses 2 hidden layers of 64 units each.
    Adjust net_arch for larger/smaller networks.
    """
    obs_size = ray_count + 2  # rays + speed + steer

    # Separate actor (pi) and critic (vf) networks — each has its own independent weights.
    return {
        "net_arch": dict(pi=[256, 128], vf=[256, 128]),
    }

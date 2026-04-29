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

    # Default: 2 layers of 64. For a small obs space this is sufficient.
    # Increase to [128, 128] or [256, 128] for more complex tasks.
    return {
        "net_arch": [256, 128],
    }

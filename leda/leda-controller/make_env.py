"""
Factory for creating SHILATE Gym environments for parallel training.
"""

from environment import ShilateEnv


def make_env(
    env_id: int,
    mqtt_host: str = "localhost",
    mqtt_port: int = 1883,
    ray_count: int = 9,
    timescale: float = 5.0,
):
    """
    Factory function that returns a callable for SubprocVecEnv.

    Usage:
        from stable_baselines3.common.vec_env import SubprocVecEnv
        envs = SubprocVecEnv([make_env(i) for i in range(4)])
    """

    def _init():
        return ShilateEnv(
            env_id=env_id,
            mqtt_host=mqtt_host,
            mqtt_port=mqtt_port,
            ray_count=ray_count,
            timescale=timescale,
        )

    return _init

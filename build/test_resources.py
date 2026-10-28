"""Opt-in limits for isolated test containers; never configure the Docker host."""
import os
import re


def compose_limits():
    result = {}
    cpu = os.environ.get('FORGE_TEST_CPUS')
    memory = os.environ.get('FORGE_TEST_MEMORY')
    if cpu:
        value = float(cpu)
        if not 0 < value < float('inf'):
            raise ValueError('FORGE_TEST_CPUS must be positive and finite')
        result['cpus'] = value
    if memory:
        if not re.fullmatch(r'[1-9][0-9]*[bkmgBKMG]?', memory):
            raise ValueError('FORGE_TEST_MEMORY must be a positive Docker memory limit')
        result['mem_limit'] = memory
    processors = os.environ.get('DOTNET_PROCESSOR_COUNT')
    if processors:
        result['environment'] = {'DOTNET_PROCESSOR_COUNT': processors}
    return result

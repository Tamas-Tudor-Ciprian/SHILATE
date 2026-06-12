#!/bin/bash
# /********************************************************************************
# * Copyright (c) 2022 Contributors to the Eclipse Foundation
# *
# * See the NOTICE file(s) distributed with this work for additional
# * information regarding copyright ownership.
# *
# * This program and the accompanying materials are made available under the
# * terms of the Apache License 2.0 which is available at
# * https://www.apache.org/licenses/LICENSE-2.0
# *
# * SPDX-License-Identifier: Apache-2.0
# ********************************************************************************/

qemu-system-x86_64 \
    -net nic,model=virtio \
    -object rng-random,filename=/dev/urandom,id=rng0 \
    -device virtio-rng-pci,rng=rng0 \
    -drive id=hd,file=sdv-image-all-qemux86-64.wic.qcow2,if=virtio,format=qcow2 \
    -serial mon:stdio \
    -serial null \
    -serial mon:vc \
    -nographic \
    -object can-bus,id=canbus0 \
    -device kvaser_pci,canbus=canbus0 \
    -drive if=pflash,format=qcow2,file=ovmf.qcow2 \
    -cpu IvyBridge \
    -machine q35 \
    -smp 8 \
    -m 4G

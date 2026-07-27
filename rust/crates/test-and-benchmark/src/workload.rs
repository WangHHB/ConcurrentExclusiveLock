pub struct MemoryWork {
    read_steps: usize,
    write_steps: usize,
    buffer: Vec<i64>,
    write_random: u32,
    state: i64,
}

impl MemoryWork {
    pub fn new(read_steps: usize, write_steps: usize, working_set_mb: usize) -> Self {
        let bytes = working_set_mb
            .checked_mul(1024 * 1024)
            .expect("memory working set overflow");
        let element_count = (bytes / std::mem::size_of::<i64>()).max(1024);
        let mut buffer = vec![0_i64; element_count];

        let mut current = 0x6A09_E667_F3BC_C909_u64 as i64;
        for (index, value) in buffer.iter_mut().enumerate() {
            current = mix(current.wrapping_add(index as i64));
            *value = current;
        }

        Self {
            read_steps,
            write_steps,
            buffer,
            write_random: 0xC801_3EA4,
            state: 0,
        }
    }

    #[inline]
    pub fn tick_read(&self, random: &mut u32) -> i64 {
        let mut result = self.state;
        let mut cursor = *random;
        for step in 0..self.read_steps {
            cursor = next_random(cursor);
            let index = cursor as usize % self.buffer.len();
            result = mix(result ^ self.buffer[index].wrapping_add(step as i64));
        }
        *random = cursor;
        result
    }

    #[inline]
    pub fn tick_write(&mut self) -> i64 {
        let mut result = self.state.wrapping_add(1);
        let mut random = self.write_random;
        for step in 0..self.write_steps {
            random = next_random(random);
            let index = random as usize % self.buffer.len();
            let next = mix(self.buffer[index] ^ result ^ step as i64);
            self.buffer[index] = next;
            result = next;
        }
        self.write_random = random;
        self.state = result;
        result
    }

    pub fn state_hash(&self) -> i64 {
        self.state
    }
}

#[inline]
pub fn next_random(mut value: u32) -> u32 {
    value ^= value << 13;
    value ^= value >> 17;
    value ^= value << 5;
    value
}

#[inline]
pub fn create_worker_seed(lock_index: usize, local_worker_index: usize) -> u32 {
    let mut value = 0x9E37_79B9_u32;
    value ^= (lock_index as u32).wrapping_mul(0x85EB_CA6B);
    value ^= (local_worker_index as u32).wrapping_mul(0xC2B2_AE35);
    value ^= value >> 16;
    value = value.wrapping_mul(0x7FEB_352D);
    value ^= value >> 15;
    value = value.wrapping_mul(0x846C_A68B);
    value ^= value >> 16;
    value
}

#[inline]
fn mix(input: i64) -> i64 {
    let mut result = input as u64;
    result ^= result >> 33;
    result = result.wrapping_mul(0xFF51_AFD7_ED55_8CCD);
    result ^= result >> 33;
    result = result.wrapping_mul(0xC4CE_B9FE_1A85_EC53);
    result ^= result >> 33;
    result as i64
}

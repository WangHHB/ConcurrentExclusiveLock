use std::time::Duration;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Mode {
    Benchmark,
    FullSemantics,
    PipelineSemantics,
    PipelineStress(Duration),
    ContentionStress(Duration),
    Endurance(Duration),
}

#[derive(Debug, Clone)]
pub struct Options {
    pub mode: Mode,
    pub lock_instances: usize,
    pub threads: usize,
    pub operations: usize,
    pub read_work: usize,
    pub write_work: usize,
    pub memory_mb: usize,
    pub semantic_workers: usize,
    pub semantic_operations: usize,
    pub semantic_seed: u64,
    pub help: bool,
}

impl Default for Options {
    fn default() -> Self {
        Self {
            mode: Mode::Benchmark,
            lock_instances: 1,
            threads: 32,
            operations: 10_000,
            read_work: 32,
            write_work: 32,
            memory_mb: 64,
            semantic_workers: 4,
            semantic_operations: 256,
            semantic_seed: 0x6A09_E667_F3BC_C909,
            help: false,
        }
    }
}

impl Options {
    pub fn parse() -> Result<Self, String> {
        let mut options = Self::default();
        let args: Vec<String> = std::env::args().skip(1).collect();
        let mut index = 0;
        let mut common_work = None;

        while index < args.len() {
            let argument = &args[index];

            match argument.as_str() {
                "-h" | "--help" => options.help = true,
                "--lock-instances" => {
                    options.lock_instances = parse_usize(take_value(&args, &mut index, argument)?, argument)?
                }
                "--threads" => options.threads = parse_usize(take_value(&args, &mut index, argument)?, argument)?,
                "--operations" => {
                    options.operations = parse_usize(take_value(&args, &mut index, argument)?, argument)?
                }
                "--work" => common_work = Some(parse_usize(take_value(&args, &mut index, argument)?, argument)?),
                "--read-work" => {
                    options.read_work = parse_usize(take_value(&args, &mut index, argument)?, argument)?
                }
                "--write-work" => {
                    options.write_work = parse_usize(take_value(&args, &mut index, argument)?, argument)?
                }
                "--memory-mb" => {
                    options.memory_mb = parse_usize(take_value(&args, &mut index, argument)?, argument)?
                }
                "--workload" => {
                    let workload = take_value(&args, &mut index, argument)?;
                    if workload != "memory" {
                        return Err(format!(
                            "unsupported workload '{workload}'; this port currently provides the C#-reference memory workload"
                        ));
                    }
                }
                "--full-semantics" | "--advanced-correctness" => {
                    set_mode(&mut options.mode, Mode::FullSemantics)?
                }
                "--pipeline-semantics" => {
                    set_mode(&mut options.mode, Mode::PipelineSemantics)?
                }
                "--pipeline-stress" => {
                    let duration = parse_duration(take_value(&args, &mut index, argument)?)?;
                    set_mode(&mut options.mode, Mode::PipelineStress(duration))?;
                }
                "--contention-stress" => {
                    let duration = parse_duration(take_value(&args, &mut index, argument)?)?;
                    set_mode(&mut options.mode, Mode::ContentionStress(duration))?;
                }
                "--endurance" => {
                    let duration = parse_duration(take_value(&args, &mut index, argument)?)?;
                    set_mode(&mut options.mode, Mode::Endurance(duration))?;
                }
                "--semantic-workers" => {
                    options.semantic_workers = parse_usize(take_value(&args, &mut index, argument)?, argument)?
                }
                "--semantic-operations" => {
                    options.semantic_operations = parse_usize(take_value(&args, &mut index, argument)?, argument)?
                }
                "--semantic-seed" => {
                    options.semantic_seed = parse_u64(take_value(&args, &mut index, argument)?, argument)?
                }
                other => return Err(format!("unknown argument: {other}")),
            }
            index += 1;
        }

        if let Some(work) = common_work {
            options.read_work = work;
            options.write_work = work;
        }
        options.validate()?;
        Ok(options)
    }

    fn validate(&self) -> Result<(), String> {
        if self.lock_instances == 0 {
            return Err("--lock-instances must be greater than 0".into());
        }
        if self.threads == 0 {
            return Err("--threads must be greater than 0".into());
        }
        if self.operations == 0 {
            return Err("--operations must be greater than 0".into());
        }
        if self.memory_mb == 0 {
            return Err("--memory-mb must be greater than 0".into());
        }
        if self.semantic_workers < 2 {
            return Err("--semantic-workers must be at least 2".into());
        }
        if self.semantic_operations == 0 {
            return Err("--semantic-operations must be greater than 0".into());
        }
        self.lock_instances
            .checked_mul(self.threads)
            .ok_or_else(|| "lock-instances × threads overflowed usize".to_string())?;
        Ok(())
    }
}

fn take_value<'a>(args: &'a [String], index: &mut usize, name: &str) -> Result<&'a str, String> {
    *index += 1;
    args.get(*index)
        .map(String::as_str)
        .ok_or_else(|| format!("missing value for {name}"))
}

fn set_mode(current: &mut Mode, next: Mode) -> Result<(), String> {
    if *current != Mode::Benchmark {
        return Err("select only one semantic/stress mode".into());
    }
    *current = next;
    Ok(())
}

fn parse_usize(value: &str, name: &str) -> Result<usize, String> {
    value
        .parse::<usize>()
        .map_err(|_| format!("invalid integer for {name}: {value}"))
}

fn parse_u64(value: &str, name: &str) -> Result<u64, String> {
    let trimmed = value.trim();
    if let Some(hex) = trimmed.strip_prefix("0x").or_else(|| trimmed.strip_prefix("0X")) {
        u64::from_str_radix(hex, 16).map_err(|_| format!("invalid integer for {name}: {value}"))
    } else {
        trimmed
            .parse::<u64>()
            .map_err(|_| format!("invalid integer for {name}: {value}"))
    }
}

pub fn parse_duration(value: &str) -> Result<Duration, String> {
    let value = value.trim();
    if let Some(raw) = value.strip_suffix("ms") {
        return raw
            .parse::<u64>()
            .map(Duration::from_millis)
            .map_err(|_| format!("invalid duration: {value}"));
    }
    if let Some(raw) = value.strip_suffix('s') {
        return raw
            .parse::<u64>()
            .map(Duration::from_secs)
            .map_err(|_| format!("invalid duration: {value}"));
    }
    if let Some(raw) = value.strip_suffix('m') {
        return raw
            .parse::<u64>()
            .map(|minutes| Duration::from_secs(minutes.saturating_mul(60)))
            .map_err(|_| format!("invalid duration: {value}"));
    }
    if let Some(raw) = value.strip_suffix('h') {
        return raw
            .parse::<u64>()
            .map(|hours| Duration::from_secs(hours.saturating_mul(3600)))
            .map_err(|_| format!("invalid duration: {value}"));
    }
    if let Some(raw) = value.strip_suffix('d') {
        return raw
            .parse::<u64>()
            .map(|days| Duration::from_secs(days.saturating_mul(86_400)))
            .map_err(|_| format!("invalid duration: {value}"));
    }

    let parts: Vec<&str> = value.split(':').collect();
    if parts.len() == 3 {
        let hours = parts[0].parse::<u64>().map_err(|_| format!("invalid duration: {value}"))?;
        let minutes = parts[1].parse::<u64>().map_err(|_| format!("invalid duration: {value}"))?;
        let seconds = parts[2].parse::<u64>().map_err(|_| format!("invalid duration: {value}"))?;
        return Ok(Duration::from_secs(
            hours.saturating_mul(3600) + minutes.saturating_mul(60) + seconds,
        ));
    }

    Err(format!(
        "invalid duration '{value}'; use 500ms, 30s, 10m, 24h, 1d, or hh:mm:ss"
    ))
}

pub fn print_help() {
    println!(
        r#"ConcurrentExclusiveLock Rust test and benchmark

Benchmark (default):
  --lock-instances <N>    independent lock + Work instances (default 1)
  --threads <N>           dedicated worker threads per lock (default 32)
  --operations <N>        operations per worker (default 10000)
  --work <N>              set both read and write steps
  --read-work <N>         memory steps in each Concurrent region (default 32)
  --write-work <N>        memory steps in each Exclusive region (default 32)
  --memory-mb <N>         shared memory per lock instance (default 64)
  --workload memory       C#-reference random-memory workload

Semantic and stress modes:
  --full-semantics
  --pipeline-semantics
  --pipeline-stress <duration>
  --contention-stress <duration>
  --endurance <duration>
  --semantic-workers <N>  workers per lock for semantic modes (default 4)
  --semantic-operations <N> rounds per worker/lock (default 256)
  --semantic-seed <N|0xHEX>

Duration examples: 500ms, 30s, 10m, 24h, 1d, 01:30:00
"#
    );
}

mod benchmark;
mod options;
mod semantic;
mod workload;

use options::{print_help, Mode, Options};

fn main() {
    if let Err(error) = run() {
        eprintln!("error: {error}");
        std::process::exit(2);
    }
}

fn run() -> Result<(), String> {
    let options = Options::parse()?;
    if options.help {
        print_help();
        return Ok(());
    }

    match options.mode {
        Mode::Benchmark => benchmark::run(&options),
        Mode::FullSemantics => semantic::run_full(&options),
        Mode::PipelineSemantics => semantic::run_pipeline_semantics(),
        Mode::PipelineStress(duration) => semantic::run_pipeline_stress(&options, duration),
        Mode::ContentionStress(duration) => semantic::run_contention_stress(&options, duration),
        Mode::Endurance(duration) => semantic::run_endurance(&options, duration),
    }
    Ok(())
}

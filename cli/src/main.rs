fn main() {
    let cli = sts2::Cli::parse();
    if sts2::cli_uses_streaming(&cli) {
        let stdout = std::io::stdout();
        let mut handle = stdout.lock();
        let exit_code = match sts2::run_cli_streaming(cli.clone(), &mut handle) {
            Ok(code) => code,
            Err(error) => {
                let rendered = error.render(cli.json);
                print!("{}", rendered.stdout);
                rendered.exit_code
            }
        };
        std::process::exit(exit_code);
    }

    let response = match sts2::run_cli(cli.clone()) {
        Ok(response) => response,
        Err(error) => error.render(cli.json),
    };

    print!("{}", response.stdout);
    std::process::exit(response.exit_code);
}

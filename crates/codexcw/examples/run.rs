//! Minimal example: run a single prompt and print the final message.
//!
//! Requires a real `codex` on `PATH`. Run with:
//! `cargo run -p codexcw --example run -- "diga oi"`

use codexcw::{Request, Runner};

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let prompt = std::env::args()
        .nth(1)
        .unwrap_or_else(|| "diga oi".to_string());

    let runner = Runner::new();
    let result = runner.run(Request::new(prompt)).await?;

    println!("{}", result.final_message);
    Ok(())
}

# Contributing Guidelines

First off—thank you for your interest in contributing to **Apps2Samsung**! This project exists thanks to community support and we welcome improvements, big or small. Whether you'd like to report a bug, suggest a feature, or contribute code, your help is appreciated.

---

## Table of Contents

1. [How You Can Contribute](#how-you-can-contribute)  
   - Reporting Bugs  
   - Requesting Features  
   - Suggesting Documentation Updates  
   - Submitting Code Changes  
2. [Getting Started](#getting-started)  
3. [Code Style & Workflow](#code-style--workflow)  
4. [Development Process](#development-process)  
   - Running Locally & Testing  
   - Staying Up to Date  
5. [License](#license)  
6. [Code of Conduct](#code-of-conduct)  
7. [Contact & Support](#contact--support)

---

## 1. How You Can Contribute

### Reporting Bugs
Please use the **Issues** tab to report bugs. Include:
- The version you're using (e.g. Stable v1.7.4, release date Sep 3, 2025)
- Your OS, .NET version, and TV model
- Steps to reproduce the issue, along with any error messages or logs

### Requesting Features
Submit new ideas or enhancements using Issues or through **Discussions**:
- Describe the use case and benefit
- Offer suggestions on how it could be implemented or designed

### Documentation Suggestions
Found something unclear in the README or wiki? Let us know!
- Submit improvements via pull request or issue
- We appreciate help with screenshots, formatting, or better explanations

### Submitting Code Changes (Pull Requests)
We welcome pull requests! Please:
- Follow the existing project structure (folders: `.github`, `Services`, `Views`, etc.)
- Keep code clean, concise, and well-commented
- Include tests or manual validation steps if applicable
- Fill out the PR template

---

## 2. Getting Started

1. Fork the repository.  
2. Clone your fork locally:  
   ```bash
   git clone https://github.com/your-username/Apps2Samsung.git
   ```  
3. Open the solution in Visual Studio or your preferred IDE. Ensure you have:
   - Microsoft Edge WebView2 Runtime  
   - .NET SDK compatible with the project  
   - Tizen Web CLI and Certificate Manager (for end-to-end testing)  
4. Run and explore the tool. Enhancements are welcome!

---

## 3. Code Style & Workflow

- Follow current C# naming conventions and file organization.  
- For user interface updates, maintain consistency with existing XAML layouts.  
- Keep commits focused and descriptive. Use messages like `Fix: …`, `Add: …`, `Refactor: …`.

---

## 4. Development Process

### Running Locally & Testing
- The app scans your local network to detect compatible Samsung TVs.  
- For Tizen 7+, a Samsung account is required for certificate generation.  
- Test your changes thoroughly—try both “manually enter IP” and automatic detection.

### Staying Up to Date
- Periodically sync with upstream:
  ```bash
  git remote add upstream https://github.com/Apps2Samsung/Apps2Samsung.git
  git fetch upstream
  git rebase upstream/beta
  ```

---

## 5. License

This repository is licensed under the **MIT License**. By contributing, you agree that your work will be licensed under the same terms.

---

## 6. Code of Conduct

Please abide by our [Code of Conduct](https://github.com/Apps2Samsung/Apps2Samsung/blob/beta/.github/CODE_OF_CONDUCT.md). Maintain a respectful, welcoming environment for all contributors.

---

## 7. Contact & Support

- **Issues**: for bugs or feature requests  
- **Discussions**: for broader conversations  
- **Wiki**: for documentation  
- **[Sponsor Page / ko-fi](https://ko-fi.com/patrickst)**: if you'd like to support the project financially  

Thanks again for your contributions—together, we can make app installation smoother and more reliable on Samsung Tizen TVs!  

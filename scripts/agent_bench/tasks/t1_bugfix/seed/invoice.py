from pricing import discounted_total


def invoice_total(lines, contract_discount=0):
    """lines: list of (unit_cents, qty). Returns the invoiced amount, in cents."""
    return discounted_total(lines, contract_discount)
